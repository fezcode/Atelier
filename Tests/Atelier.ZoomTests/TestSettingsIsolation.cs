using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Atelier.Hoswl;
using Xunit;
using Xunit.Sdk;

[assembly: Atelier.ZoomTests.FreshSettings]

// The headless platform runs every windowed test on one dispatcher thread, and they now
// share a settings file. Serialising the whole assembly keeps that sharing honest.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Atelier.ZoomTests;

/// <summary>
/// Settings are written by ordinary UI actions now -- closing the metadata pane persists
/// it -- and nearly every test builds a real MainWindow. Two things follow: the suite must
/// never reach the developer's own %APPDATA%\fezcode\Atelier\settings.json, and one test's
/// saved choice must not decide what the next test's window starts with.
/// </summary>
internal static class TestSettingsIsolation
{
    private static string? _current;

    [ModuleInitializer]
    internal static void Redirect() => Fresh();

    /// <summary>Points settings at an unused temp file, discarding the previous one.</summary>
    internal static void Fresh()
    {
        if (_current != null) { try { File.Delete(_current); } catch { } }
        _current = Path.Combine(Path.GetTempPath(), "atelier-tests-" + Guid.NewGuid().ToString("N") + ".json");
        UserSettings.PathOverride = _current;
    }
}

/// <summary>
/// Applied to the assembly, so every test starts from a fresh install's settings without
/// each test class having to remember.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
public sealed class FreshSettingsAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest) => TestSettingsIsolation.Fresh();
}
