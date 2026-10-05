using AIQuota.Localization;

namespace AIQuota;

/// <summary>
/// Asks the user to confirm installing an available update, showing the commit log since
/// the running version (date and subject of each commit, oldest first) in a scrollable,
/// read-only text pane.
/// </summary>
public sealed class UpdateConfirmDialog : Form
{
    private UpdateConfirmDialog(NewVersionInfo update)
    {
        Text = Strings.AppTitle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        ClientSize = new Size(480, 360);

        var promptText = Strings.ConfirmUpdatePrompt(update.Version);
        var promptPadding = new Padding(12, 12, 12, 8);
        var promptWidth = ClientSize.Width - promptPadding.Horizontal;
        var promptTextHeight = TextRenderer.MeasureText(
            promptText, Font, new Size(promptWidth, int.MaxValue), TextFormatFlags.WordBreak).Height;

        var promptLabel = new Label
        {
            Text = promptText,
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = promptTextHeight + promptPadding.Vertical,
            Padding = promptPadding,
        };

        var changelogBody = update.Changelog is { Length: > 0 } entries ? entries : Strings.NoChangelogAvailable;
        var notesText = $"{Strings.ChangelogHeader(update.PreviousVersion)}\n\n{changelogBody}";

        var notesBox = new TextBox
        {
            Text = NormalizeNewlines(notesText),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Margin = new Padding(12),
            Font = new Font(FontFamily.GenericMonospace, 9f),
        };
        var notesPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 8) };
        notesPanel.Controls.Add(notesBox);

        var yesButton = new Button
        {
            Text = Strings.ConfirmUpdateYes,
            DialogResult = DialogResult.Yes,
            Width = 110,
        };
        var noButton = new Button
        {
            Text = Strings.ConfirmUpdateNo,
            DialogResult = DialogResult.No,
            Width = 110,
        };

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 48,
            Padding = new Padding(12),
        };
        buttonPanel.Controls.Add(noButton);
        buttonPanel.Controls.Add(yesButton);

        Controls.Add(notesPanel);
        Controls.Add(buttonPanel);
        Controls.Add(promptLabel);

        AcceptButton = yesButton;
        CancelButton = noButton;
    }

    /// <summary>Shows the dialog and returns whether the user chose to install the update.</summary>
    public static bool Confirm(NewVersionInfo update)
    {
        using var dialog = new UpdateConfirmDialog(update);
        return dialog.ShowDialog() == DialogResult.Yes;
    }

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");
}
