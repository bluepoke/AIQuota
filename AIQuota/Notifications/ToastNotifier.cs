using System.Runtime.InteropServices;
using AIQuota.Localization;
using Microsoft.Toolkit.Uwp.Notifications;

namespace AIQuota.Notifications;

/// <summary>
/// Windows toast notifications with action buttons, via the Windows Community Toolkit's
/// <see cref="ToastNotificationManagerCompat"/>.
///
/// <list type="bullet">
/// <item><b>Zip build (unpackaged)</b>: on first use the toolkit registers an
/// AppUserModelID for this exe (named and iconed after it, so Windows' notification
/// settings list it as "AI Quota") and a COM activator that brings button clicks back to
/// the running instance - see <see cref="RemoveRegistration"/> for what that leaves
/// behind.</item>
/// <item><b>MSIX build (packaged)</b>: Windows uses the package identity, and the activator
/// is declared in <c>Package/AppxManifest.xml</c> (toastNotificationActivation + comServer
/// extensions), where the toolkit reads its CLSID from.</item>
/// </list>
///
/// A toast clicked after the app has exited starts it with <c>-ToastActivated</c>; it then
/// starts normally and handles the click (<see cref="Program"/> drops such a start silently
/// if an instance is already running). Each notification kind has a fixed tag, so a newer
/// one replaces the previous one in the notification center. Do Not Disturb and the app's
/// notification settings apply as for any toast. If registering or showing a toast fails,
/// the notification goes to the <see cref="BalloonNotifier"/> fallback instead.
/// </summary>
internal sealed class ToastNotifier : INotifier
{
    /// <summary>Passed by Windows when a toast click (re)starts the app.</summary>
    public const string ActivationArgument = "-ToastActivated";

    private const string Group = "aiquota";
    private const string ActionKey = "action";
    private const string OpenUsagePageAction = "openUsagePage";
    private const string InstallAction = "install";

    private readonly INotifier _fallback;
    private readonly Action<NotificationAction> _onAction;
    private readonly SynchronizationContext _uiContext;

    private ToastNotifier(INotifier fallback, Action<NotificationAction> onAction, SynchronizationContext uiContext)
    {
        _fallback = fallback;
        _onAction = onAction;
        _uiContext = uiContext;

        // Subscribing registers the activator (and, unpackaged, the AppUserModelID); it must
        // happen early, so a click that started the app is delivered too.
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
    }

    /// <summary>Toasts, or - if the toolkit can't register them - balloon tips on
    /// <paramref name="notifyIcon"/>. <paramref name="onAction"/> is called on
    /// <paramref name="uiContext"/>.</summary>
    public static INotifier Create(NotifyIcon notifyIcon, Action<NotificationAction> onAction, SynchronizationContext uiContext)
    {
        var balloons = new BalloonNotifier(notifyIcon, onAction);
        try
        {
            return new ToastNotifier(balloons, onAction, uiContext);
        }
        catch (Exception)
        {
            return balloons;
        }
    }

    public void ShowUsageAlert(UsageAlert alert, string text, DateTimeOffset? expiresAt)
    {
        var tag = alert switch
        {
            UsageAlert.Session => "session",
            UsageAlert.Weekly => "weekly",
            _ => "forecast",
        };

        Show(tag, expiresAt,
            builder => builder
                .AddText(Strings.AppTitle)
                .AddText(text)
                .AddButton(new ToastButton().SetContent(Strings.ToastOpenUsagePage).AddArgument(ActionKey, OpenUsagePageAction))
                .AddButton(new ToastButtonDismiss(Strings.ToastDismiss)),
            () => _fallback.ShowUsageAlert(alert, text, expiresAt));
    }

    public void ShowNewVersion(string version)
    {
        Show("update", expiresAt: null,
            builder => builder
                .AddText(Strings.ToastNewVersionTitle)
                .AddText(Strings.NotificationNewVersion(version))
                .AddButton(new ToastButton().SetContent(Strings.ToastInstall).AddArgument(ActionKey, InstallAction))
                .AddButton(new ToastButtonDismiss(Strings.ToastLater)),
            () => _fallback.ShowNewVersion(version));
    }

    private static void Show(string tag, DateTimeOffset? expiresAt, Action<ToastContentBuilder> build, Action fallback)
    {
        try
        {
            var builder = new ToastContentBuilder();
            build(builder);
            builder.Show(toast =>
            {
                toast.Tag = tag;
                toast.Group = Group;
                if (expiresAt is { } expiry && expiry > DateTimeOffset.Now.AddMinutes(1))
                    toast.ExpirationTime = expiry;
            });
        }
        catch (Exception)
        {
            fallback();
        }
    }

    /// <summary>Runs on a background thread. A click on the toast itself (no button) comes
    /// with no arguments and opens the flyout; "Dismiss"/"Later" are system buttons that
    /// never reach the app.</summary>
    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var arguments = ToastArguments.Parse(e.Argument);
        var action = arguments.TryGetValue(ActionKey, out string value)
            ? value switch
            {
                OpenUsagePageAction => NotificationAction.OpenUsagePage,
                InstallAction => NotificationAction.Install,
                _ => NotificationAction.OpenFlyout,
            }
            : NotificationAction.OpenFlyout;

        _uiContext.Post(_ => _onAction(action), null);
    }

    /// <summary>Whether the app runs from the MSIX package (which brings its own notification
    /// registration and removes it on uninstall).</summary>
    public static bool IsPackaged
    {
        get
        {
            var length = 0;
            return NativeMethods.GetCurrentPackageFullName(ref length, null) != NativeMethods.APPMODEL_ERROR_NO_PACKAGE;
        }
    }

    /// <summary>Zip build only: removes everything the toolkit registered for this exe and
    /// clears its notifications from the notification center - the registry keys
    /// <c>HKCU\Software\Classes\AppUserModelId\{AUMID}</c>,
    /// <c>HKCU\Software\Classes\CLSID\{activator}</c> and
    /// <c>HKCU\Software\Classes\AppID\{activator}</c>, and the icon copy under
    /// <c>%LOCALAPPDATA%\ToastNotificationManagerCompat\Apps\{AUMID}</c>. The next toast
    /// would register it again, so the app exits right after.</summary>
    public static void RemoveRegistration() => ToastNotificationManagerCompat.Uninstall();

    private static class NativeMethods
    {
        public const int APPMODEL_ERROR_NO_PACKAGE = 15700;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
    }
}
