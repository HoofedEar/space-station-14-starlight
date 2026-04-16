#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests;
using Content.IntegrationTests.Pair;
using Robust.Client.GameObjects;
using Robust.Server.Player;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.UnitTesting.Pool;

namespace RobustMapEditor.Core.Session;

/// <summary>
/// Holds a <see cref="TestPair"/> for the lifetime of one open map, with the map's
/// entities actually alive in the pair's server so we can mutate them between edits
/// and re-render / re-serialize without re-parsing YAML. Contrast with Phase 1's
/// <c>MapIo</c>, which used <c>MapPainter</c>'s throwaway pair and dropped the
/// loaded entities as soon as painting finished.
/// </summary>
/// <remarks>
/// The pool settings are <c>Fresh = true, Destructive = true</c> — Fresh ensures we
/// start from a clean slate (no leftover state from another test/editor session),
/// Destructive makes the pool dispose the pair on return rather than recycle it,
/// which matters because we spin up potentially long-lived pairs and don't want
/// another caller picking up our mutated map.
/// </remarks>
public sealed class DocumentSession : IAsyncDisposable
{
    /// <summary>Number of ticks to sync after load so entities finish initializing.
    /// Mirrors <c>MapPainter</c>'s behavior. Smaller values risk rendering before
    /// sprites are resolved; larger values let more game logic run.</summary>
    private const int PostLoadTicks = 200;

    public TestPair Pair { get; }
    public string FilePath { get; private set; }

    /// <summary>Map root entities loaded from the file. For round-trip save we
    /// serialize these recursively.</summary>
    public IReadOnlyList<EntityUid> MapUids { get; }
    public IReadOnlyList<Entity<MapGridComponent>> Grids { get; }

    private DocumentSession(
        TestPair pair,
        string filePath,
        IReadOnlyList<EntityUid> mapUids,
        IReadOnlyList<Entity<MapGridComponent>> grids)
    {
        Pair = pair;
        FilePath = filePath;
        MapUids = mapUids;
        Grids = grids;
    }

    public static async Task<DocumentSession> OpenAsync(string filePath, ITestContextLike? testContext = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Map file not found: {filePath}", filePath);

        testContext ??= new ExternalTestContext("RobustMapEditor.DocumentSession", new StringWriter());

        var pair = await PoolManager.GetServerClient(
            new PoolSettings
            {
                DummyTicker = false,
                Connected   = true,
                // Destructive + Fresh: take a clean pair and don't let it go back into
                // the pool where another caller could pick up our edited map state.
                Destructive = true,
                Fresh       = true,
            },
            testContext);

        List<EntityUid> mapUids;
        List<Entity<MapGridComponent>> grids;

        try
        {
            using var stream = File.OpenRead(filePath);

            LoadResult? result = null;
            await pair.Server.WaitPost(() =>
            {
                var opts = new MapLoadOptions
                {
                    DeserializationOptions =
                    {
                        // File might be a grid or a map; accept both and don't warn on orphans.
                        LogOrphanedGrids = false,
                    },
                };

                if (!pair.Server.System<MapLoaderSystem>().TryLoadGeneric(stream, filePath, out result, opts))
                    throw new IOException($"File {filePath} could not be read");
            });

            mapUids = result!.Maps.Select(m => m.Owner).ToList();
            grids   = result.Grids.ToList();

            // Hide the default client player sprite so it doesn't show up in every render.
            // Mirrors MapPainter.SetupView.
            await pair.Client.WaitPost(() =>
            {
                if (pair.Client.EntMan.TryGetComponent(pair.Client.PlayerMan.LocalEntity, out SpriteComponent? sprite))
                {
                    pair.Client.System<SpriteSystem>()
                        .SetVisible((pair.Client.PlayerMan.LocalEntity.Value, sprite), false);
                }
            });

            // Brief settle before we start poking server state.
            await pair.RunTicksSync(10);
            await Task.WhenAll(pair.Client.WaitIdleAsync(), pair.Server.WaitIdleAsync());

            // One-time setup that MapPainter.Paint does before its first render:
            //   - delete the ghost player entity so it doesn't show up on any grid
            //   - snap grids to Angle.Zero world rotation so rendering is axis-aligned
            // We do this here (not per-render) because it shouldn't repeat on edit-triggered re-renders.
            await pair.Server.WaitPost(() =>
            {
                var sEntityManager = pair.Server.ResolveDependency<IEntityManager>();
                var sPlayerManager = pair.Server.ResolveDependency<IPlayerManager>();
                var xformQuery = sEntityManager.GetEntityQuery<TransformComponent>();
                var xformSystem = sEntityManager.System<SharedTransformSystem>();

                var playerEntity = sPlayerManager.Sessions.SingleOrDefault()?.AttachedEntity;
                if (playerEntity.HasValue)
                    sEntityManager.DeleteEntity(playerEntity.Value);

                foreach (var (uid, _) in grids)
                {
                    var gridXform = xformQuery.GetComponent(uid);
                    xformSystem.SetWorldRotation(gridXform, Angle.Zero);
                }
            });

            // Deeper settle: let physics/AI/shuttle logic stabilize post-setup. Matches MapPainter.
            await pair.RunTicksSync(PostLoadTicks);
            await Task.WhenAll(pair.Client.WaitIdleAsync(), pair.Server.WaitIdleAsync());

            // MapPainter resets rotations a second time after the long sync to counter anything
            // that might have nudged the grids during those ticks (shuttle AI etc.). Same idea here.
            await pair.Server.WaitPost(() =>
            {
                var sEntityManager = pair.Server.ResolveDependency<IEntityManager>();
                var xformQuery = sEntityManager.GetEntityQuery<TransformComponent>();
                var xformSystem = sEntityManager.System<SharedTransformSystem>();

                foreach (var (uid, _) in grids)
                {
                    var xform = xformQuery.GetComponent(uid);
                    xformSystem.SetWorldRotation(xform, Angle.Zero);
                }
            });
        }
        catch
        {
            await pair.DisposeAsync();
            throw;
        }

        return new DocumentSession(pair, filePath, mapUids, grids);
    }

    public async ValueTask DisposeAsync()
    {
        // Pair was created with Destructive=true, so dispose tears down the server/client rather
        // than recycling — our mutated map state goes with it, which is what we want.
        await Pair.DisposeAsync();
    }
}
