using AIQuota.Localization;

namespace AIQuota;

/// <summary>Asks for an account's short name (#13), e.g. "Work". An empty name means "use
/// the account's own name".</summary>
internal sealed class RenameAccountDialog : Form
{
    private readonly TextBox _nameBox;

    private RenameAccountDialog(string label, string? shortName)
    {
        Text = Strings.RenameAccountTitle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(400, 150);

        var prompt = new Label
        {
            Text = Strings.RenameAccountPrompt(label),
            AutoSize = false,
            Location = new Point(12, 12),
            Size = new Size(376, 48),
        };

        _nameBox = new TextBox
        {
            Text = shortName ?? "",
            MaxLength = 40,
            Location = new Point(12, 66),
            Width = 376,
        };

        var okButton = new Button { Text = Strings.DialogOk, DialogResult = DialogResult.OK, Width = 100, Location = new Point(182, 108) };
        var cancelButton = new Button { Text = Strings.DialogCancel, DialogResult = DialogResult.Cancel, Width = 100, Location = new Point(288, 108) };

        Controls.Add(prompt);
        Controls.Add(_nameBox);
        Controls.Add(okButton);
        Controls.Add(cancelButton);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    /// <summary>Shows the dialog. Returns false if it was cancelled; otherwise <paramref
    /// name="newShortName"/> is the trimmed name, or null for an empty one.</summary>
    public static bool Ask(string label, string? shortName, out string? newShortName)
    {
        using var dialog = new RenameAccountDialog(label, shortName);
        newShortName = null;
        if (dialog.ShowDialog() != DialogResult.OK)
            return false;
        var name = dialog._nameBox.Text.Trim();
        newShortName = name.Length > 0 ? name : null;
        return true;
    }
}
