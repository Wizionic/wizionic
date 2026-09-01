namespace App.Services;

public static class SecurityEmailContent
{
    public static (string Subject, string Text, string Html) PasswordChanged(string? ip, DateTime utc)
    {
        var when = utc.ToString("u");
        var extra = string.IsNullOrEmpty(ip) ? "" : $" Approximate source: {ip}.";
        var text = $@"Your Wizionic password was changed on {when} UTC.{extra}

If this was you, no action is needed. Other devices will need to sign in again.

If this was not you, sign in on a device you trust, change the password, and use Sign out other devices on the Account page.

-- The Wizionic Team";
        return ("Your Wizionic password was changed", text, Wrap(text));
    }

    public static (string Subject, string Text, string Html) PasswordRemoved(string? ip, DateTime utc)
    {
        var when = utc.ToString("u");
        var extra = string.IsNullOrEmpty(ip) ? "" : $" Approximate source: {ip}.";
        var text = $@"Your Wizionic password was removed on {when} UTC.{extra}

Sign-in is email login code only until you add a new password. Two-factor sign-in was turned off.

If this was you, no action is needed. Other devices will need to sign in again.

If this was not you, sign in on a device you trust, add a password, and use Sign out other devices on the Account page.

-- The Wizionic Team";
        return ("Your Wizionic password was removed", text, Wrap(text));
    }

    public static (string Subject, string Text, string Html) NewDevice(string? deviceName, string? ip, DateTime utc)
    {
        var name = string.IsNullOrWhiteSpace(deviceName) ? "a new device" : deviceName.Trim();
        var when = utc.ToString("u");
        var extra = string.IsNullOrEmpty(ip) ? "" : $" Approximate source: {ip}.";
        var text = $@"Wizionic signed in on {name} at {when} UTC.{extra}

If this was you, no action is needed.

If this was not you, change your password on a device you trust. That signs other devices out.

-- The Wizionic Team";
        return ("New Wizionic sign-in", text, Wrap(text));
    }

    public static (string Subject, string Text, string Html) TwoFactorChanged(bool enabled)
    {
        var text = enabled
            ? "Two-factor sign-in was turned on for your Wizionic account. Save your recovery codes in a safe place.\n\nIf this was not you, change your password on a device you trust.\n\n-- The Wizionic Team"
            : "Two-factor sign-in was turned off for your Wizionic account. Other devices will need to sign in again.\n\nIf this was not you, change your password on a device you trust.\n\n-- The Wizionic Team";
        return (
            enabled ? "Wizionic two-factor is on" : "Wizionic two-factor was turned off",
            text,
            Wrap(text));
    }

    public static (string Subject, string Text, string Html) PhoneChanged(string action)
    {
        var text = $"A phone number was {action} on your Wizionic two-factor settings.\n\nIf this was not you, change your password on a device you trust.\n\n-- The Wizionic Team";
        return ("Wizionic two-factor phone updated", text, Wrap(text));
    }

    private static string Wrap(string text)
    {
        var html = System.Net.WebUtility.HtmlEncode(text).Replace("\n", "<br />");
        return $@"<!DOCTYPE html><html><body style=""font-family:system-ui,sans-serif;line-height:1.5;color:#222;max-width:520px;""><p>{html}</p></body></html>";
    }
}
