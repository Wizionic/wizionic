namespace App.Services;

/// <summary>
/// Login email: copy/paste code only. No sign-in URL — those opened the wrong app/browser
/// and email scanners would consume one-time tokens.
/// </summary>
public static class LoginEmailContent
{
    public const string Subject = "Your Wizionic Login Code";

    public static (string Text, string Html) Build(string loginCode)
    {
        var textBody = $@"Hello,

You (or someone using your email) requested to log in to Wizionic.

Your one-time login code:

{loginCode}

Enter this code in the Wizionic app or on the website. It expires in 15 minutes and can only be used once.

If you did not request this login, you can safely ignore this email.

-- The Wizionic Team
";

        var htmlBody = $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"" />
<style>
  body {{ font-family: system-ui, -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, sans-serif; line-height: 1.5; color: #222; max-width: 520px; margin: 0 auto; padding: 16px; }}
  .code {{ font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, ""Liberation Mono"", ""Courier New"", monospace; font-size: 1.75rem; font-weight: 700; letter-spacing: 0.2em; background: #f4f4f4; padding: 14px 18px; border-radius: 6px; display: inline-block; margin: 12px 0; }}
  .footer {{ font-size: 0.85em; color: #666; margin-top: 24px; }}
</style>
</head>
<body>
  <p>Hello,</p>
  <p>You (or someone using your email) requested to log in to <strong>Wizionic</strong>.</p>
  <p>Your one-time login code:</p>
  <p class=""code"">{loginCode}</p>
  <p style=""font-size:0.9em;"">Enter this code in the Wizionic app or on the website. It expires in 15 minutes and can only be used once.</p>
  <p style=""font-size:0.9em;"">If you did not request this, you can safely ignore this email.</p>
  <div class=""footer"">-- The Wizionic Team</div>
</body>
</html>";

        return (textBody, htmlBody);
    }
}
