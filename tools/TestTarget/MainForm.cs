using System.ComponentModel;

namespace TestTarget;

/// <summary>
/// Deterministic test vehicle for the DeskDx locator tests. Deliberately mimics the properties of
/// migrated VB6/WinForms applications: designer names are exposed as UIA AutomationId by the
/// modern WinForms provider, most controls have no <see cref="Control.AccessibleName"/> (so the
/// UIA Name falls back to the caption), and identical captions exist.
/// </summary>
/// <remarks>
/// Layout notes for the locator tests:
/// <list type="bullet">
///   <item>"Speichern" exists exactly once (btnSave) — the unique-match baseline.</item>
///   <item>"Duplikat" exists twice (btnDup1/btnDup2) — strict mode violation.</item>
///   <item>"GruppenKnopf" exists once in "Gruppe A" and once in "Gruppe B" — proves that a chained
///     locator only searches the subtree of its scope.</item>
///   <item>More than 20 direct children — truncation of the timeout state dump.</item>
/// </list>
/// </remarks>
public sealed class MainForm : Form
{
    private readonly Button _btnDelayed;
    private readonly Label _statusLabel;
    private readonly Panel _pnlCover;
    private bool _added;

    public MainForm()
    {
        Name = "MainForm";
        Text = "DeskDx TestTarget";
        ClientSize = new Size(700, 600);
        StartPosition = FormStartPosition.CenterScreen;

        // ── Row 1: the primary locator targets ───────────────────────────────
        var btnSave = new Button
        {
            Name = "btnSave",
            Text = "Speichern",
            Location = new Point(20, 20),
            Size = new Size(120, 32),
        };

        var txtKunde = new TextBox
        {
            Name = "txtKunde",
            Location = new Point(156, 24),
            Size = new Size(160, 24),
        };

        var lblKunde = new Label
        {
            Name = "lblKunde",
            Text = "Kunde:",
            Location = new Point(326, 28),
            AutoSize = true,
        };

        var btnId = new Button
        {
            Name = "btnId",
            Text = "MitId",
            // AccessibleName has priority over Text and becomes the UIA Name property.
            AccessibleName = "aid-btnId",
            Location = new Point(420, 20),
            Size = new Size(120, 32),
        };

        // ── Row 2: strict mode violation (identical caption) ─────────────────
        var btnDup1 = new Button
        {
            Name = "btnDup1",
            Text = "Duplikat",
            Location = new Point(20, 70),
            Size = new Size(120, 32),
        };

        var btnDup2 = new Button
        {
            Name = "btnDup2",
            Text = "Duplikat",
            Location = new Point(156, 70),
            Size = new Size(120, 32),
        };

        // ── Row 3: later steps (wait, dialog, late appearance) ───────────────
        _statusLabel = new Label
        {
            Name = "lblStatus",
            Text = "bereit",
            Location = new Point(20, 420),
            AutoSize = true,
        };

        _btnDelayed = new Button
        {
            Name = "btnDelayed",
            Text = "Langsam",
            Enabled = false,
            Location = new Point(20, 120),
            Size = new Size(120, 32),
        };

        var btnMsgBox = new Button
        {
            Name = "btnMsgBox",
            Text = "Meldung",
            Location = new Point(156, 120),
            Size = new Size(120, 32),
        };
        btnMsgBox.Click += (_, _) => MessageBox.Show(
            this,
            "DeskDx TestTarget Meldung",
            "Meldung",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        // Yes/No dialog: the answer is remembered in the status label so a test can verify which
        // button the registry (or the test itself) clicked.
        var btnMsgBoxYesNo = new Button
        {
            Name = "btnMsgBoxYesNo",
            Text = "Frage",
            Location = new Point(428, 120),
            Size = new Size(120, 32),
        };
        btnMsgBoxYesNo.Click += (_, _) =>
        {
            var answer = MessageBox.Show(
                this,
                "Wirklich löschen?",
                "Frage",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            _statusLabel.Text = answer == DialogResult.Yes ? "Ja geklickt" : "Nein geklickt";
        };

        // Non-modal second form: the dialog watcher must NOT touch it (D5).
        var btnNewForm = new Button
        {
            Name = "btnNewForm",
            Text = "Zweitfenster",
            Location = new Point(564, 120),
            Size = new Size(120, 32),
        };
        btnNewForm.Click += (_, _) =>
        {
            var form2 = new Form2 { Owner = this };
            form2.Show(this);
            _statusLabel.Text = "Zweitfenster offen";
        };

        var btnDynamic = new Button
        {
            Name = "btnDynamic",
            Text = "Mehr",
            Location = new Point(292, 120),
            Size = new Size(120, 32),
        };
        btnDynamic.Click += (_, _) => AddLateButton();

        // ── Row 4: two sibling scopes with an identical child caption ────────
        var groupA = CreateGroup("grpA", "Gruppe A", new Point(20, 170), "btnA1");
        var groupB = CreateGroup("grpB", "Gruppe B", new Point(360, 170), "btnB1");

        // ── Fillers: ensure the window has more than 20 direct children so the
        //    `scope children:` dump of a timeout gets truncated. ──────────────
        var fillers = new List<Control>();
        for (var i = 1; i <= 12; i++)
        {
            fillers.Add(new Button
            {
                Name = $"btnFiller{i:00}",
                Text = $"Filler {i:00}",
                Location = new Point(20 + ((i - 1) % 4 * 136), 280 + ((i - 1) / 4 * 40)),
                Size = new Size(120, 32),
            });
        }

        // ── Row 5: actionability-gate scenarios (Step 3) ─────────────────────
        // Disabled text box: Fill must fail on the Editable/Enabled check.
        var txtDisabled = new TextBox
        {
            Name = "txtDisabled",
            Text = "Gesperrt",
            Enabled = false,
            Location = new Point(156, 340),
            Size = new Size(160, 24),
        };

        // Cover panel: fixed position exactly over btnSave (same Location/Size), initially hidden.
        // Clicking btnCover makes it visible -> btnSave becomes covered (ReceivesEvents test).
        // The panel is added last, so it sits above btnSave in z-order.
        var btnCover = new Button
        {
            Name = "btnCover",
            Text = "Abdecken",
            Location = new Point(428, 340),
            Size = new Size(120, 32),
        };

        var cmbAuswahl = new ComboBox
        {
            Name = "cmbAuswahl",
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(156, 380),
            Size = new Size(160, 24),
        };
        cmbAuswahl.Items.AddRange(["Erste", "Zweite", "Dritte"]);

        // Multiline text box: the Windows TextBox control normalizes line breaks to \r\n, which is
        // exactly the case the ToHaveValue/ToHaveText normalization must handle (Step 4 E7).
        var txtMultiline = new TextBox
        {
            Name = "txtMultiline",
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            Location = new Point(340, 380),
            Size = new Size(200, 80),
        };

        _pnlCover = new Panel
        {
            Name = "pnlCover",
            Visible = false,
            Location = new Point(btnSave.Location.X, btnSave.Location.Y),
            Size = new Size(btnSave.Size.Width, btnSave.Size.Height),
            BackColor = SystemColors.ControlDark,
        };
        btnCover.Click += (_, _) =>
        {
            // Toggle: click 1 covers btnSave (Step 3 G8), click 2 uncovers it (Step 4 E8).
            _pnlCover.Visible = !_pnlCover.Visible;
            if (_pnlCover.Visible)
            {
                _pnlCover.BringToFront();
                _statusLabel.Text = "btnSave verdeckt";
            }
            else
            {
                _statusLabel.Text = "btnSave sichtbar";
            }
        };

        Controls.AddRange(
        [
            btnSave,
            txtKunde,
            lblKunde,
            btnId,
            btnDup1,
            btnDup2,
            _btnDelayed,
            btnMsgBox,
            btnMsgBoxYesNo,
            btnNewForm,
            btnDynamic,
            groupA,
            groupB,
            _statusLabel,
            txtDisabled,
            btnCover,
            cmbAuswahl,
            txtMultiline,
            _pnlCover,
        ]);
        Controls.AddRange(fillers.ToArray());

        // Enable the delayed button two seconds after the form is first shown.
        var enableTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        enableTimer.Tick += (_, _) =>
        {
            enableTimer.Stop();
            enableTimer.Dispose();
            _btnDelayed.Enabled = true;
            _statusLabel.Text = "Langsam aktiviert";
        };
        Shown += (_, _) => enableTimer.Start();
    }

    private static GroupBox CreateGroup(string name, string text, Point location, string childName)
    {
        var group = new GroupBox
        {
            Name = name,
            Text = text,
            Location = location,
            Size = new Size(300, 90),
        };

        group.Controls.Add(new Button
        {
            Name = childName,
            Text = "GruppenKnopf",
            Location = new Point(20, 26),
            Size = new Size(140, 32),
        });

        return group;
    }

    private void AddLateButton()
    {
        if (_added)
        {
            return;
        }

        _added = true;
        Controls.Add(new Button
        {
            Name = "btnAdded",
            Text = "Hinzugefügt",
            Location = new Point(428, 120),
            Size = new Size(120, 32),
        });
        _statusLabel.Text = "Hinzugefügt angelegt";
    }
}
