namespace ChatfishApp.Services;

/// <summary>
/// Shared login email body for web (magic link) and native (copy/paste code) clients.
/// </summary>
public static class LoginEmailContent
{
    public const string Subject = "Your Chatfish Login Code";

    public static (string Text, string Html) Build(string loginCode, string magicLinkUrl)
    {
        var textBody = $@"Hello,

You (or someone using your email) requested to log in to Chatfish.

Your one-time login code:

{loginCode}

Enter this code in the Chatfish app (web or mobile) to sign in.

On the web, you can also click this link to sign in directly:

{magicLinkUrl}

This code and link expire in 15 minutes and can only be used once.

If you did not request this login, you can safely ignore this email.

-- The Chatfish Team
";

        var htmlBody = $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"" />
<style>
  body {{ font-family: system-ui, -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, sans-serif; line-height: 1.5; color: #222; max-width: 520px; margin: 0 auto; padding: 16px; }}
  .code {{ font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, ""Liberation Mono"", ""Courier New"", monospace; font-size: 1.75rem; font-weight: 700; letter-spacing: 0.2em; background: #f4f4f4; padding: 14px 18px; border-radius: 6px; display: inline-block; margin: 12px 0; }}
  .btn {{ display: inline-block; background: #3BA7FF; color: white !important; padding: 14px 24px; border-radius: 6px; text-decoration: none; font-weight: 600; margin: 12px 0; }}
  .link {{ word-break: break-all; font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, ""Liberation Mono"", ""Courier New"", monospace; background: #f4f4f4; padding: 10px; border-radius: 4px; display: block; margin: 12px 0; font-size: 0.9em; }}
  .footer {{ font-size: 0.85em; color: #666; margin-top: 24px; }}
</style>
</head>
<body>
  <p>Hello,</p>
  <p>You (or someone using your email) requested to log in to <strong>Chatfish</strong>.</p>

  <p>Your one-time login code:</p>
  <p class=""code"">{loginCode}</p>
  <p style=""font-size:0.9em;"">Enter this code in the Chatfish app (web or mobile).</p>

  <p>On the web, you can also click the button below:</p>
  <p><a class=""btn"" href=""{magicLinkUrl}"">Log in to Chatfish</a></p>

  <p>Or copy and paste this link:</p>
  <p class=""link"">{magicLinkUrl}</p>

  <p style=""font-size:0.9em;"">The code and link expire in 15 minutes and can only be used once.</p>
  <p style=""font-size:0.9em;"">If you did not request this, you can safely ignore this email.</p>

  <div class=""footer"">-- The Chatfish Team</div>
</body>
</html>";

        return (textBody, htmlBody);
    }
}