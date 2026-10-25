using System.Diagnostics;

namespace DeskDx.Tests;

/// <summary>
/// Step 6 acceptance tests S1–S8: the canonical UIA snapshot and its baseline diff.
/// </summary>
/// <remarks>
/// The baselines live in <c>tests/DeskDx.Tests/snapshots/</c> and are committed: the git diff over a
/// baseline is the evidence of what a migration module changed about a form.
/// </remarks>
[Collection("TestTarget")]
public sealed class SnapshotTests
{
    private const string MainFormBaseline = "main-form";

    /// <summary>
    /// Controls whose UIA Name is volatile: the status label and the disabled text box change when
    /// the enable timer fires. They are excluded so the diff test measures the structural change it
    /// is about, not the timer. (That Names are volatile at all is a WinForms provider property.)
    /// </summary>
    private static readonly string[] VolatileControls = ["lblStatus", "txtDisabled"];

    private readonly TestTargetFixture _fixture;

    public SnapshotTests(TestTargetFixture fixture)
    {
        _fixture = fixture;
        // The baselines belong to the test project, next to the tests, not in bin/.
        SnapshotStore.BaseDirectory = TestTargetFixture.TestProjectRoot;
    }

    private static string CreateTraceRoot()
        => Path.Combine(Path.GetTempPath(), "DeskDx.Tests", Guid.NewGuid().ToString("N"));

    private AppSession Start()
        => AppSession.Launch(
            _fixture.ExecutablePath,
            options: new AppSessionOptions { TraceDirectory = CreateTraceRoot() });

    /// <summary>Runs <paramref name="action"/> while the update environment variable is set.</summary>
    private static void WithUpdateMode(Action action)
    {
        var previous = Environment.GetEnvironmentVariable(SnapshotStore.UpdateEnvironmentVariable);
        Environment.SetEnvironmentVariable(SnapshotStore.UpdateEnvironmentVariable, "1");
        try
        {
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(SnapshotStore.UpdateEnvironmentVariable, previous);
        }
    }

    // ── S1: canonical rendering ──────────────────────────────────────────────
    [Fact]
    public void S1_ToUiaSnapshot_RendersCanonicalTree()
    {
        using var session = Start();

        var snapshot = session.GetByName("DeskDx TestTarget").ToUiaSnapshot();
        var lines = snapshot.Replace("\r\n", "\n").Split('\n');

        // Root line is the main window itself.
        Assert.Equal("Window \"DeskDx TestTarget\" [AutomationId=MainForm]", lines[0]);

        // Controls with the right indentation (2 spaces per level).
        Assert.Contains("  Button \"Speichern\" [AutomationId=btnSave]", lines);
        Assert.Contains("  Edit \"\" [AutomationId=txtKunde]", lines);
        Assert.Contains("  Group \"Gruppe A\" [AutomationId=grpA]", lines);
        Assert.Contains("    Button \"GruppenKnopf\" [AutomationId=btnA1]", lines);

        // Structure only: no position, handle or timestamp information.
        Assert.DoesNotContain(lines, line => line.Contains("Rect="));
        Assert.DoesNotContain(lines, line => line.Contains("enabled)"));
    }

    // ── S2: two snapshots are byte-identical ─────────────────────────────────
    [Fact]
    public void S2_TwoSnapshots_AreByteIdentical()
    {
        using var session = Start();

        var first = session.GetByName("DeskDx TestTarget").ToUiaSnapshot();
        var second = session.GetByName("DeskDx TestTarget").ToUiaSnapshot();

        Assert.Equal(first, second);
    }

    // ── S3: update mode creates the baseline, then a normal run matches ─────
    [Fact]
    public void S3_UpdateMode_CreatesBaseline_ThenNormalRunMatches()
    {
        var baselineName = "s3-" + Guid.NewGuid().ToString("N");
        var baselinePath = SnapshotStore.BaselinePath(baselineName);
        try
        {
            using (var session = Start())
            {
                WithUpdateMode(() => session.GetByName("DeskDx TestTarget").ToMatchUiaSnapshot(baselineName));
                Assert.True(File.Exists(baselinePath), $"expected the baseline at {baselinePath}");

                // A second run without the flag must pass against the created baseline.
                session.GetByName("DeskDx TestTarget").ToMatchUiaSnapshot(baselineName);
            }
        }
        finally
        {
            if (File.Exists(baselinePath))
            {
                File.Delete(baselinePath);
            }
        }
    }

    // ── S4: the core test — a structural change fails the diff ──────────────
    [Fact]
    public void S4_CoverPanelAppearing_FailsTheDiffWithExactlyThatLine()
    {
        var baselineName = "s4-" + Guid.NewGuid().ToString("N");
        var baselinePath = SnapshotStore.BaselinePath(baselineName);
        var actualPath = SnapshotStore.MismatchActualPath(baselineName);

        try
        {
            using var session = Start();

            // Create the baseline from the uncovered form.
            WithUpdateMode(() => session.GetByName("DeskDx TestTarget").ToMatchUiaSnapshot(baselineName));

            // Cover btnSave — the panel appears in the tree.
            session.GetByAutomationId("btnCover").Invoke();

            var error = Assert.Throws<DeskDxExpectError>(
                () => session.GetByName("DeskDx TestTarget").ToMatchUiaSnapshot(baselineName, new UiaSnapshotOptions
                {
                    Timeout = TimeSpan.FromMilliseconds(500),
                    ExcludeIds = VolatileControls,
                }));

            Assert.Contains(error.CallLog, line => line.Contains("+  Pane \"\" [AutomationId=pnlCover]"));
            Assert.Contains(error.CallLog, line => line.Contains("baseline: " + baselinePath));
            Assert.Contains(error.CallLog, line => line.Contains("actual:   " + actualPath));
            Assert.True(File.Exists(actualPath), $"expected the mismatch artifact at {actualPath}");
        }
        finally
        {
            if (File.Exists(baselinePath))
            {
                File.Delete(baselinePath);
            }

            if (File.Exists(actualPath))
            {
                File.Delete(actualPath);
            }

            var diffPath = SnapshotStore.MismatchDiffPath(baselineName);
            if (File.Exists(diffPath))
            {
                File.Delete(diffPath);
            }
        }
    }

    // ── S5: ExcludeIds suppresses the volatile panel ────────────────────────
    [Fact]
    public void S5_ExcludeIds_MakesTheCoveredFormMatchAgain()
    {
        var baselineName = "s5-" + Guid.NewGuid().ToString("N");
        var baselinePath = SnapshotStore.BaselinePath(baselineName);

        try
        {
            using var session = Start();

            var exclude = new UiaSnapshotOptions
            {
                ExcludeIds = ["pnlCover", .. VolatileControls],
                Timeout = TimeSpan.FromSeconds(2),
            };
            WithUpdateMode(() => session.GetByName("DeskDx TestTarget").ToMatchUiaSnapshot(baselineName, exclude));

            session.GetByAutomationId("btnCover").Invoke();

            // The panel is excluded by AutomationId, so the covered form still matches.
            session.GetByName("DeskDx TestTarget").ToMatchUiaSnapshot(baselineName, exclude);

            // And the exclusion really removed the line from the rendering.
            var snapshot = session.GetByName("DeskDx TestTarget").ToUiaSnapshot(exclude);
            Assert.DoesNotContain("pnlCover", snapshot, StringComparison.Ordinal);
            Assert.DoesNotContain("lblStatus", snapshot, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(baselinePath))
            {
                File.Delete(baselinePath);
            }
        }
    }

    // ── S6: the failure message carries paths and diff lines ────────────────
    [Fact]
    public void S6_FailureMessage_ContainsPathsAndDiffLines()
    {
        var baselineName = "s6-" + Guid.NewGuid().ToString("N");
        var baselinePath = SnapshotStore.BaselinePath(baselineName);
        var actualPath = SnapshotStore.MismatchActualPath(baselineName);

        try
        {
            using var session = Start();
            WithUpdateMode(() => session.GetByName("DeskDx TestTarget").ToMatchUiaSnapshot(baselineName));
            session.GetByAutomationId("btnCover").Invoke();

            var error = Assert.Throws<DeskDxExpectError>(
                () => session.GetByName("DeskDx TestTarget").ToMatchUiaSnapshot(baselineName, new UiaSnapshotOptions
                {
                    Timeout = TimeSpan.FromMilliseconds(500),
                    ExcludeIds = VolatileControls,
                }));

            Assert.StartsWith(
                "expect(locator(name=\"DeskDx TestTarget\")).ToMatchUiaSnapshot(\"" + baselineName + "\") failed after 500ms",
                error.Message,
                StringComparison.Ordinal);
            Assert.Contains("baseline: " + baselinePath, error.Message, StringComparison.Ordinal);
            Assert.Contains("actual:   " + actualPath, error.Message, StringComparison.Ordinal);

            // The diff is unified-diff style: an added line and a removed line must both be present.
            Assert.Contains("+  Pane \"\" [AutomationId=pnlCover]", error.Message, StringComparison.Ordinal);
            Assert.Contains("\n-", error.Message, StringComparison.Ordinal);
            Assert.Contains("\n+", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(baselinePath);
            File.Delete(actualPath);
            File.Delete(SnapshotStore.MismatchDiffPath(baselineName));
        }
    }

    // ── S7: IncludeState is visibly effective ───────────────────────────────
    [Fact]
    public void S7_IncludeState_ShowsTheDisabledButton()
    {
        using var session = Start();

        var structure = session.GetByName("DeskDx TestTarget").ToUiaSnapshot();
        var withState = session.GetByName("DeskDx TestTarget").ToUiaSnapshot(new UiaSnapshotOptions { IncludeState = true });

        Assert.DoesNotContain("(disabled)", structure, StringComparison.Ordinal);
        Assert.Contains("Button \"Langsam\" [AutomationId=btnDelayed] (disabled)", withState, StringComparison.Ordinal);
        Assert.Contains("Button \"Speichern\" [AutomationId=btnSave] (enabled)", withState, StringComparison.Ordinal);

        // After the enable timer the state snapshot differs, i.e. the flag is really effective.
        session.GetByAutomationId("btnDelayed").Resolve();
        Expect.That(session.GetByAutomationId("btnDelayed")).ToBeEnabled();
        var afterEnable = session.GetByName("DeskDx TestTarget").ToUiaSnapshot(new UiaSnapshotOptions { IncludeState = true });

        Assert.Contains("Button \"Langsam\" [AutomationId=btnDelayed] (enabled)", afterEnable, StringComparison.Ordinal);
        Assert.NotEqual(withState, afterEnable);
    }

    // ── S8: a chained locator snapshots only its subtree ────────────────────
    [Fact]
    public void S8_ChainedScope_SnapshotsOnlyItsSubtree()
    {
        using var session = Start();

        var snapshot = session.GetByName("Gruppe A").ToUiaSnapshot();

        Assert.StartsWith("Group \"Gruppe A\" [AutomationId=grpA]", snapshot, StringComparison.Ordinal);
        Assert.Contains("Button \"GruppenKnopf\" [AutomationId=btnA1]", snapshot, StringComparison.Ordinal);
        // Main-window controls must not appear.
        Assert.DoesNotContain("btnSave", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("MainForm", snapshot, StringComparison.Ordinal);
    }

    // ── S9: the settle check is applied and can be disabled ────────────────
    [Fact]
    public void S9_Settle_IsAppliedByDefaultAndCanBeDisabled()
    {
        using var session = Start();

        // Settling costs at least one extra capture plus the delay, so the default call is measurably
        // slower than the same call with settling switched off.
        var settleDelay = TimeSpan.FromMilliseconds(300);
        var options = new UiaSnapshotOptions { SettleDelay = settleDelay };

        var settledWatch = Stopwatch.StartNew();
        var settled = session.GetByName("DeskDx TestTarget").ToUiaSnapshot(options);
        settledWatch.Stop();

        var rawOptions = new UiaSnapshotOptions { Settle = false };
        var rawWatch = Stopwatch.StartNew();
        var raw = session.GetByName("DeskDx TestTarget").ToUiaSnapshot(rawOptions);
        rawWatch.Stop();

        // Both must describe the same tree; settling changes when a capture is accepted, not what a
        // capture contains.
        Assert.Equal(raw, settled);

        // The settled call must have waited at least roughly the settle delay.
        Assert.True(
            settledWatch.Elapsed >= settleDelay,
            $"expected the settle delay to be observed, got {settledWatch.ElapsedMilliseconds}ms");
        Assert.True(
            rawWatch.Elapsed < settledWatch.Elapsed,
            $"expected the unsettled capture to be faster ({rawWatch.ElapsedMilliseconds}ms) "
            + $"than the settled one ({settledWatch.ElapsedMilliseconds}ms)");
    }

    // ── S10: the element-addressed entry point settles too ─────────────────
    [Fact]
    public void S10_ElementAddressedSnapshot_MatchesTheLocatorSnapshot()
    {
        using var session = Start();

        var viaLocator = session.GetByName("DeskDx TestTarget").ToUiaSnapshot();

        var handle = session.MainWindow?.Properties.NativeWindowHandle.ValueOrDefault ?? IntPtr.Zero;
        Assert.NotEqual(IntPtr.Zero, handle);

        var viaHandle = session.ToUiaSnapshot(handle);
        Assert.Equal(viaLocator, viaHandle);
    }

    // ── Extra: a missing baseline fails with the update hint ────────────────
    [Fact]
    public void Extra_MissingBaseline_FailsWithUpdateHint()
    {
        using var session = Start();
        var baselineName = "missing-" + Guid.NewGuid().ToString("N");

        var error = Assert.Throws<DeskDxExpectError>(
            () => session.GetByName("DeskDx TestTarget").ToMatchUiaSnapshot(baselineName));

        Assert.Contains("baseline missing:", error.Message, StringComparison.Ordinal);
        Assert.Contains("DESKDX_SNAPSHOT_UPDATE=1", error.Message, StringComparison.Ordinal);

        // The actual data is still recorded so the baseline can be inspected and adopted.
        var actualPath = SnapshotStore.MismatchActualPath(baselineName);
        Assert.True(File.Exists(actualPath));
        File.Delete(actualPath);
        File.Delete(SnapshotStore.MismatchDiffPath(baselineName));
    }

    // ── Extra: the committed main-form baseline ──────────────────────────────
    [Fact]
    public void Extra_CommittedMainFormBaseline_Matches()
    {
        using var session = Start();
        var options = new UiaSnapshotOptions
        {
            ExcludeIds = VolatileControls,
            Timeout = TimeSpan.FromSeconds(2),
        };

        // Creates the committed baseline when DESKDX_SNAPSHOT_UPDATE=1; otherwise it must match.
        session.GetByName("DeskDx TestTarget").ToMatchUiaSnapshot(MainFormBaseline, options);

        var baselinePath = SnapshotStore.BaselinePath(MainFormBaseline);
        Assert.True(File.Exists(baselinePath), $"expected the committed baseline at {baselinePath}");
    }
}
