namespace TestTarget;

/// <summary>
/// A deliberately non-modal second window. The dialog watcher must ignore it: only modal windows
/// count as dialogs (Step 5 D5).
/// </summary>
public sealed class Form2 : Form
{
    public Form2()
    {
        Name = "Form2";
        Text = "Zweitfenster";
        ClientSize = new Size(360, 160);
        StartPosition = FormStartPosition.CenterParent;

        Controls.Add(new Label
        {
            Name = "lblForm2",
            Text = "Nicht-modales Zweitfenster",
            Location = new Point(20, 40),
            AutoSize = true,
        });
    }
}
