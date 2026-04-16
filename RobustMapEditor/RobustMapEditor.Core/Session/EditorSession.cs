#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests;
using Content.IntegrationTests.Pair;
using Content.Shared.Maps;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;
using Robust.UnitTesting.Pool;

namespace RobustMapEditor.Core.Session;

/// <summary>
/// Owns the headless server/client pair that backs the editor. Wraps
/// <see cref="PoolManager"/> so the UI doesn't see any test-harness types.
/// </summary>
/// <remarks>
/// We intentionally follow the same bootstrap recipe as Content.MapRenderer: call
/// <see cref="PoolManager.Startup"/> once per process, grab a <see cref="TestPair"/>,
/// and hold it for the lifetime of the session. Getting the pair is slow (seconds),
/// so the UI should show a splash while <see cref="InitializeAsync"/> runs.
/// </remarks>
public sealed class EditorSession : IAsyncDisposable
{
    private static int _startupCount;
    private static readonly object StartupLock = new();

    private TestPair? _pair;

    public TestPair Pair => _pair
        ?? throw new InvalidOperationException("EditorSession has not been initialized. Call InitializeAsync first.");

    public bool IsInitialized => _pair != null;

    public async Task InitializeAsync(ITestContextLike? testContext = null)
    {
        if (_pair != null)
            return;

        lock (StartupLock)
        {
            if (_startupCount++ == 0)
                PoolManager.Startup();
        }

        testContext ??= new ExternalTestContext("RobustMapEditor", new StringWriter());
        _pair = await PoolManager.GetServerClient(testContext: testContext);
    }

    /// <summary>
    /// Enumerates all gameMap prototype IDs (excluding test-only prototypes).
    /// Used by the UI to populate the "Open prototype map" list, and as a
    /// simple proof-of-life after session bootstrap.
    /// </summary>
    public IReadOnlyList<string> GetGameMapPrototypeIds()
    {
        var pair = Pair;
        var protoMan = pair.Server.ResolveDependency<IPrototypeManager>();

        return protoMan
            .EnumeratePrototypes<GameMapPrototype>()
            .Where(m => !pair.IsTestPrototype(m))
            .Select(m => m.ID)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (_pair != null)
        {
            await _pair.DisposeAsync();
            _pair = null;
        }

        lock (StartupLock)
        {
            if (--_startupCount == 0)
                PoolManager.Shutdown();
        }
    }
}
