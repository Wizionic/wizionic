using App.Core.Auth;
using App.Core.Storage;
using App.Data;
using App.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json.Serialization;


namespace App.Apis;

/// <summary>
/// Minimal API endpoints exposed specifically for the WASM client.
/// 
/// Auth surface:
/// - POST /api/auth/request-magic-link  (PUBLIC) → hashed login code emailed (no clickable link; account created on verify)
/// - POST /api/auth/verify-code         (PUBLIC) → exchange email+code for cookie session
/// - POST /api/auth/reset-password      (PUBLIC) → email+code clears password+2FA (forgot password) then issues a cookie
/// - POST /api/auth/login-password      (PUBLIC) → email+password login (generic errors; no existence/password-set clues)
/// - POST /api/auth/2fa/send            (PUBLIC) → send SMS/email code for a password-verified challenge
/// - POST /api/auth/2fa/verify          (PUBLIC) → complete 2FA and issue cookie
/// - POST /api/auth/logout              (PUBLIC) → revoke this session + clear cookie
/// - GET  /api/auth/me                  (protected) → identity (email, id, has key, has password, 2FA flags)
/// - GET  /api/auth/sessions            (protected) → list revocable sessions
/// - POST /api/auth/sessions/revoke     (protected) → sign out one other session
/// - POST /api/auth/sessions/revoke-others (protected) → sign out every other session
/// - POST /api/auth/set-password        (protected) → set/change account password (emails + revokes other sessions)
/// - POST /api/auth/2fa/settings        (protected) → enable/disable login 2FA
/// - POST /api/auth/2fa/enroll-sms      (protected) → start Twilio phone enrollment
/// - POST /api/auth/2fa/confirm-sms     (protected) → confirm Twilio phone enrollment
/// - POST /api/auth/2fa/remove-phone    (protected) → drop enrolled SMS number
/// - POST /api/auth/verify-password     (protected) → check password (notebook unlock; never 2FA)
/// - GET  /api/user/encryption-key      (protected) → per-user AES-GCM key for client storage/sync
/// - GET  /api/keys                     (protected) → server-stored provider keys (decrypted) so WASM can import them
///
/// Plus tool proxies (under /api/tools) so that agentic/tool-calling in WASM chat can use
/// web search and page summarization without browser CORS problems. The real work happens
/// on the server (reusing the same AppTools as the interactive server chat).
///
/// /api/tools/* requires the same AppAuth cookie as /api/auth/me. Public auth endpoints
/// (magic-link, verify-code, reset-password, password, 2FA, logout) stay unauthenticated so sign-in works.
/// 
/// The server never stores WASM conversation history (per design).
/// 
/// Keep this file as the single place for all WASM-facing HTTP APIs so Program.cs stays small.
/// </summary>
public static class WasmApiEndpoints
{
    public static IEndpointRouteBuilder MapWasmApis(this IEndpointRouteBuilder endpoints)
    {
        // Public (no auth) auth endpoints for the WASM client.
        // /api/auth/request-magic-link lets a WASM page (the new root landing page) initiate
        // a magic link without a cookie. A real email is sent (via IEmailSender / MailKit)
        // containing a nice clickable button/link PLUS the raw URL so the user can copy it
        // if they are on a different device or browser than the one where they started login.
        // The raw token/link is NEVER returned to the browser (security).
        var publicAuth = endpoints.MapGroup("/api/auth");
        publicAuth.MapPost("/request-magic-link", async (MagicLinkService magic, IEmailSender emailSender, AuthThrottleService throttle, RequestMagicLink req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email))
                return Results.BadRequest("Email is required.");

            var email = EmailNormalizer.Normalize(req.Email);
            if (string.IsNullOrEmpty(email))
                return Results.BadRequest("Email is required.");

            if (!throttle.IsCodeSendAllowed(email))
            {
                return Results.Json(new
                {
                    sent = false,
                    message = AuthThrottleService.CodeRateMessage
                }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            var loginCode = await magic.CreateLoginCodeAsync(email);
            var sent = await emailSender.SendLoginEmailAsync(email, loginCode);
            if (!sent)
            {
                return Results.Json(new
                {
                    sent = false,
                    message = "Could not send a login code right now. Try again in a moment."
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new
            {
                sent = true,
                message = "If that address can receive mail, a login code is on the way. Check your inbox and spam folder."
            });
        }).RequireRateLimiting("auth");

        publicAuth.MapPost("/verify-code", async (HttpContext ctx, MagicLinkService magic, TwoFactorAuthService twoFactor, AppDbContext db, AuthSessionService sessions, UserDeviceService devices, IEmailSender emailSender, AuthThrottleService throttle, VerifyLoginCode req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Code))
                return Results.BadRequest("Email and code are required.");

            var email = EmailNormalizer.Normalize(req.Email);
            var (locked, lockMsg) = await throttle.CheckLockoutAsync(db, email);
            if (locked)
                return Results.Json(new { success = false, message = lockMsg }, statusCode: StatusCodes.Status429TooManyRequests);

            var existing = await UserLookup.ByEmailAsync(db, email);
            if (existing != null && existing.TwoFactorEnabled && !twoFactor.HasLiveChallenge(existing))
            {
                return Results.Json(new
                {
                    success = false,
                    message = "This account requires your password, then a second factor. Sign in with your password first."
                }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var ready = await magic.ValidateLoginCodeAsync(email, req.Code.Trim());
            if (ready == null)
            {
                await throttle.RegisterFailureAsync(db, email);
                return Results.Json(new { success = false, message = AuthThrottleService.GenericCodeFail }, statusCode: StatusCodes.Status401Unauthorized);
            }

            twoFactor.ClearChallenge(ready);
            await twoFactor.PersistAsync();
            await throttle.RegisterSuccessAsync(db, ready);
            await AuthSignInHelper.CompleteSignInAsync(ctx, ready, sessions, devices, emailSender);

            return Results.Ok(new
            {
                success = true,
                email = ready.Email,
                message = "Signed in successfully."
            });
        }).RequireRateLimiting("auth");

        // Forgot-password: same emailed login code as sign-in, but this path is allowed even when
        // 2FA is on. Normal verify-code still requires password-then-2FA. Never rotates the
        // encryption key. Generic errors — do not reveal whether the email exists or had a password.
        publicAuth.MapPost("/reset-password", async (HttpContext ctx, MagicLinkService magic, AppDbContext db, AuthSessionService sessions, UserDeviceService devices, IEmailSender emailSender, AuthThrottleService throttle, VerifyLoginCode req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Code))
                return Results.BadRequest("Email and code are required.");

            var email = EmailNormalizer.Normalize(req.Email);
            var (locked, lockMsg) = await throttle.CheckLockoutAsync(db, email);
            if (locked)
                return Results.Json(new { success = false, message = lockMsg }, statusCode: StatusCodes.Status429TooManyRequests);

            var ready = await magic.ValidateLoginCodeAsync(email, req.Code.Trim());
            if (ready == null)
            {
                await throttle.RegisterFailureAsync(db, email);
                return Results.Json(new { success = false, message = AuthThrottleService.GenericCodeFail }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var cleared = ClearPasswordAndTwoFactor(ready);
            if (cleared)
            {
                await db.SaveChangesAsync();
                await sessions.RevokeOthersAsync(ready.Id, currentRawSid: null);
                var (subj, text, html) = SecurityEmailContent.PasswordRemoved(
                    ctx.Connection.RemoteIpAddress?.ToString(), DateTime.UtcNow);
                try { await emailSender.SendSecurityNoticeAsync(ready.Email, subj, text, html); }
                catch { /* never fail the reset */ }
            }

            await throttle.RegisterSuccessAsync(db, ready);
            await AuthSignInHelper.CompleteSignInAsync(ctx, ready, sessions, devices, emailSender);

            return Results.Ok(new
            {
                success = true,
                email = ready.Email,
                hasPassword = false,
                message = cleared
                    ? "Password removed. You can add a new one."
                    : "Signed in successfully."
            });
        }).RequireRateLimiting("auth");

        publicAuth.MapPost("/logout", async (HttpContext ctx, AuthSessionService sessions) =>
        {
            await AuthSignInHelper.SignOutUserAsync(ctx, sessions);
            return Results.Ok(new { signedOut = true });
        });

        // Password login. Always returns the same generic error so callers cannot tell
        // whether the email exists or whether a password has been set.
        publicAuth.MapPost("/login-password", async (HttpContext ctx, MagicLinkService magic, AppDbContext db, TwoFactorAuthService twoFactor, AuthSessionService sessions, UserDeviceService devices, IEmailSender emailSender, AuthThrottleService throttle, LoginWithPassword req) =>
        {
            const string genericFail = AuthThrottleService.GenericAuthFail;

            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return Results.Unauthorized();

            var email = EmailNormalizer.Normalize(req.Email);
            var (locked, lockMsg) = await throttle.CheckLockoutAsync(db, email);
            if (locked)
                return Results.Json(new { success = false, message = lockMsg }, statusCode: StatusCodes.Status429TooManyRequests);

            var user = await UserLookup.ByEmailAsync(db, email);

            if (user == null || string.IsNullOrEmpty(user.PasswordHash))
            {
                PasswordHashService.DummyVerify(req.Password);
                await throttle.RegisterFailureAsync(db, email);
                return Results.Json(new { success = false, message = genericFail }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!PasswordHashService.Verify(req.Password, user.PasswordHash))
            {
                await throttle.RegisterFailureAsync(db, email);
                return Results.Json(new { success = false, message = genericFail }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (PasswordHashService.NeedsRehash(user.PasswordHash))
            {
                user.PasswordHash = PasswordHashService.Hash(req.Password);
                await db.SaveChangesAsync();
            }

            var remember = req.RememberDevice;
            var deviceId = AuthSessionService.ReadDeviceId(ctx);
            var skipTwoFactor = user.TwoFactorEnabled && await devices.SkipsTwoFactorAsync(user.Id, deviceId);

            if (user.TwoFactorEnabled && !skipTwoFactor)
            {
                var challengeId = twoFactor.CreateChallenge(user);
                await twoFactor.PersistAsync();

                var method = twoFactor.PreferredMethod(user);
                var (sent, sendErr) = await twoFactor.SendAsync(user, method, ctx);
                if (!sent && method == "sms")
                {
                    method = "email";
                    (sent, sendErr) = await twoFactor.SendAsync(user, method, ctx);
                }

                if (!sent)
                {
                    twoFactor.ClearChallenge(user);
                    await twoFactor.PersistAsync();
                    return Results.Json(new { success = false, message = sendErr ?? genericFail }, statusCode: StatusCodes.Status401Unauthorized);
                }

                return Results.Ok(new
                {
                    success = true,
                    requiresTwoFactor = true,
                    challengeId,
                    methods = twoFactor.MethodsFor(user),
                    maskedPhone = TwoFactorAuthService.MaskPhone(user.TwoFactorPhoneE164),
                    preferredMethod = method
                });
            }

            var ready = await magic.EnsureUserReadyForSignInAsync(user);
            if (ready == null)
                return Results.Json(new { success = false, message = genericFail }, statusCode: StatusCodes.Status401Unauthorized);

            await throttle.RegisterSuccessAsync(db, ready);
            await AuthSignInHelper.CompleteSignInAsync(ctx, ready, sessions, devices, emailSender, remember);

            return Results.Ok(new
            {
                success = true,
                email = ready.Email,
                message = "Signed in successfully."
            });
        }).RequireRateLimiting("auth");

        publicAuth.MapPost("/2fa/send", async (HttpContext ctx, TwoFactorAuthService twoFactor, TwoFactorSendRequest req) =>
        {
            if (string.Equals(TwoFactorAuthService.NormalizeMethod(req.Method), "recovery", StringComparison.Ordinal))
                return Results.Json(new { success = false, message = "Recovery codes are entered on this screen. They are not sent." }, statusCode: StatusCodes.Status400BadRequest);

            var user = await twoFactor.FindByChallengeAsync(req.ChallengeId);
            if (user == null)
                return Results.Json(new { success = false, message = "Session expired. Sign in with your password again." }, statusCode: StatusCodes.Status401Unauthorized);

            var (ok, err) = await twoFactor.SendAsync(user, req.Method, ctx);
            return ok
                ? Results.Ok(new { success = true })
                : Results.Json(new { success = false, message = err ?? "Could not send a code." }, statusCode: StatusCodes.Status400BadRequest);
        }).RequireRateLimiting("auth");

        publicAuth.MapPost("/2fa/verify", async (HttpContext ctx, MagicLinkService magic, TwoFactorAuthService twoFactor, AuthSessionService sessions, UserDeviceService devices, IEmailSender emailSender, AuthThrottleService throttle, AppDbContext db, TwoFactorVerifyRequest req) =>
        {
            const string genericFail = "Incorrect or expired code.";

            var user = await twoFactor.FindByChallengeAsync(req.ChallengeId);
            if (user == null)
                return Results.Json(new { success = false, message = "Session expired. Sign in with your password again." }, statusCode: StatusCodes.Status401Unauthorized);

            var (ok, err) = await twoFactor.VerifyAsync(user, req.Method, req.Code);
            if (!ok)
            {
                await throttle.RegisterFailureAsync(db, user.Email);
                return Results.Json(new { success = false, message = err ?? genericFail }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var ready = await magic.EnsureUserReadyForSignInAsync(user);
            if (ready == null)
                return Results.Json(new { success = false, message = genericFail }, statusCode: StatusCodes.Status401Unauthorized);

            twoFactor.ClearChallenge(ready);
            await twoFactor.PersistAsync();
            await throttle.RegisterSuccessAsync(db, ready);
            await AuthSignInHelper.CompleteSignInAsync(ctx, ready, sessions, devices, emailSender, req.RememberDevice);

            return Results.Ok(new
            {
                success = true,
                email = ready.Email,
                message = "Signed in successfully."
            });
        }).RequireRateLimiting("auth");

        var group = endpoints.MapGroup("/api").RequireAuthorization();
        var toolsGroup = endpoints.MapGroup("/api/tools").RequireAuthorization();

        group.MapGet("/auth/me", async (ClaimsPrincipal user, KeyProtectionService protector, AppDbContext db, ITwilioVerifyService twilio) =>
        {
            var email = user.Identity?.Name;
            if (string.IsNullOrEmpty(email))
                return Results.Unauthorized();

            // Lightweight check: does the DB row for this email have a (protected) LocalEncryptionKey?
            // The actual unprotected key bytes are returned by the dedicated endpoint below.
            var row = await db.Users.AsNoTracking()
                .Where(u => u.Email.ToLower() == email.ToLower())
                .Select(u => new
                {
                    HasKey = u.LocalEncryptionKey != null,
                    HasPassword = u.PasswordHash != null && u.PasswordHash != "",
                    u.TwoFactorEnabled,
                    u.TwoFactorPhoneE164,
                    u.RecoveryCodesJson
                })
                .FirstOrDefaultAsync();

            if (row == null)
                return Results.Unauthorized();

            return Results.Ok(new
            {
                Email = email,
                Id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                HasLocalEncryptionKey = row.HasKey,
                HasPassword = row.HasPassword,
                TwoFactorEnabled = row.TwoFactorEnabled,
                HasTwoFactorPhone = !string.IsNullOrEmpty(row.TwoFactorPhoneE164),
                TwoFactorPhoneMasked = TwoFactorAuthService.MaskPhone(row.TwoFactorPhoneE164),
                SmsTwoFactorAvailable = twilio.IsConfigured,
                HasRecoveryCodes = !string.IsNullOrEmpty(row.RecoveryCodesJson)
            });
        });

        // Set or change password for the currently signed-in user.
        group.MapPost("/auth/set-password", async (HttpContext ctx, ClaimsPrincipal principal, AppDbContext db, AuthSessionService sessions, HaveIBeenPwnedService pwned, IEmailSender emailSender, SetPasswordRequest req) =>
        {
            var email = principal.Identity?.Name;
            if (string.IsNullOrEmpty(email))
                return Results.Unauthorized();

            if (!PasswordRules.TryValidate(req.Password, out var reason))
                return Results.BadRequest(new { message = reason });

            if (!string.Equals(req.Password, req.ConfirmPassword, StringComparison.Ordinal))
                return Results.BadRequest(new { message = "Passwords do not match." });

            if (await pwned.IsPwnedAsync(req.Password!))
                return Results.BadRequest(new { message = "This password appears in a known data breach. Choose a different one." });

            var user = await UserLookup.ByEmailAsync(db, email);
            if (user == null)
                return Results.Unauthorized();

            var changingExisting = !string.IsNullOrEmpty(user.PasswordHash);
            if (changingExisting)
            {
                if (string.IsNullOrEmpty(req.CurrentPassword) || !PasswordHashService.Verify(req.CurrentPassword, user.PasswordHash))
                    return Results.BadRequest(new { message = "Current password is incorrect." });
            }

            user.PasswordHash = PasswordHashService.Hash(req.Password!);
            await db.SaveChangesAsync();

            if (changingExisting)
            {
                await sessions.RevokeOthersAsync(user.Id, AuthSessionService.ReadSid(principal));
                var (subj, text, html) = SecurityEmailContent.PasswordChanged(
                    ctx.Connection.RemoteIpAddress?.ToString(), DateTime.UtcNow);
                try { await emailSender.SendSecurityNoticeAsync(user.Email, subj, text, html); }
                catch { /* never fail the password change */ }
            }

            return Results.Ok(new { success = true, hasPassword = true, message = "Password saved." });
        });

        group.MapPost("/auth/2fa/settings", async (ClaimsPrincipal principal, AppDbContext db, AuthSessionService sessions, IEmailSender emailSender, [FromBody] TwoFactorSettingsRequest req) =>
        {
            var email = principal.Identity?.Name;
            if (string.IsNullOrEmpty(email))
                return Results.Unauthorized();

            var user = await UserLookup.ByEmailAsync(db, email);
            if (user == null)
                return Results.Unauthorized();

            if (req.Enabled)
            {
                if (string.IsNullOrEmpty(user.PasswordHash))
                    return Results.BadRequest(new { message = "Set a password before turning on two-factor sign-in." });

                user.TwoFactorEnabled = true;
                var codes = RecoveryCodeService.Generate();
                RecoveryCodeService.StoreHashes(user, codes);
                await db.SaveChangesAsync();

                var (subj, text, html) = SecurityEmailContent.TwoFactorChanged(true);
                try { await emailSender.SendSecurityNoticeAsync(user.Email, subj, text, html); }
                catch { }

                return Results.Ok(new { success = true, twoFactorEnabled = true, recoveryCodes = codes });
            }

            if (string.IsNullOrEmpty(user.PasswordHash)
                || string.IsNullOrEmpty(req.CurrentPassword)
                || !PasswordHashService.Verify(req.CurrentPassword, user.PasswordHash))
            {
                return Results.BadRequest(new { message = "Current password is required to turn off two-factor sign-in." });
            }

            user.TwoFactorEnabled = false;
            user.TwoFactorPhoneE164 = null;
            user.TwoFactorChallengeHash = null;
            user.TwoFactorChallengeExpiresAt = null;
            RecoveryCodeService.Clear(user);
            await db.SaveChangesAsync();
            await sessions.RevokeOthersAsync(user.Id, AuthSessionService.ReadSid(principal));

            var off = SecurityEmailContent.TwoFactorChanged(false);
            try { await emailSender.SendSecurityNoticeAsync(user.Email, off.Subject, off.Text, off.Html); }
            catch { }

            return Results.Ok(new { success = true, twoFactorEnabled = false, hasTwoFactorPhone = false });
        });

        group.MapPost("/auth/2fa/enroll-sms", async (ClaimsPrincipal principal, TwoFactorAuthService twoFactor, EnrollSmsRequest req) =>
        {
            var email = principal.Identity?.Name;
            if (string.IsNullOrEmpty(email))
                return Results.Unauthorized();

            var (ok, err) = await twoFactor.StartPhoneEnrollmentAsync(req.Phone ?? "");
            return ok
                ? Results.Ok(new { success = true })
                : Results.BadRequest(new { message = err ?? "Could not send an SMS code." });
        });

        group.MapPost("/auth/2fa/confirm-sms", async (ClaimsPrincipal principal, AppDbContext db, TwoFactorAuthService twoFactor, IEmailSender emailSender, ConfirmSmsRequest req) =>
        {
            var email = principal.Identity?.Name;
            if (string.IsNullOrEmpty(email))
                return Results.Unauthorized();

            var user = await UserLookup.ByEmailAsync(db, email);
            if (user == null)
                return Results.Unauthorized();

            if (string.IsNullOrEmpty(user.PasswordHash))
                return Results.BadRequest(new { message = "Set a password before adding SMS." });

            var (ok, err) = await twoFactor.ConfirmPhoneEnrollmentAsync(user, req.Phone ?? "", req.Code ?? "");
            if (!ok)
                return Results.BadRequest(new { message = err ?? "Incorrect or expired code." });

            var notice = SecurityEmailContent.PhoneChanged("added");
            try { await emailSender.SendSecurityNoticeAsync(user.Email, notice.Subject, notice.Text, notice.Html); }
            catch { }

            return Results.Ok(new
            {
                success = true,
                twoFactorEnabled = true,
                hasTwoFactorPhone = true,
                twoFactorPhoneMasked = TwoFactorAuthService.MaskPhone(user.TwoFactorPhoneE164)
            });
        });

        group.MapPost("/auth/2fa/remove-phone", async (ClaimsPrincipal principal, AppDbContext db, IEmailSender emailSender, RemovePhoneRequest req) =>
        {
            var email = principal.Identity?.Name;
            if (string.IsNullOrEmpty(email))
                return Results.Unauthorized();

            var user = await UserLookup.ByEmailAsync(db, email);
            if (user == null)
                return Results.Unauthorized();

            if (string.IsNullOrEmpty(user.PasswordHash)
                || string.IsNullOrEmpty(req.CurrentPassword)
                || !PasswordHashService.Verify(req.CurrentPassword, user.PasswordHash))
            {
                return Results.BadRequest(new { message = "Current password is required to remove the phone number." });
            }

            user.TwoFactorPhoneE164 = null;
            await db.SaveChangesAsync();
            var notice = SecurityEmailContent.PhoneChanged("removed");
            try { await emailSender.SendSecurityNoticeAsync(user.Email, notice.Subject, notice.Text, notice.Html); }
            catch { }
            return Results.Ok(new { success = true, hasTwoFactorPhone = false });
        });

        // Verify the account password (used to unlock password-protected notebooks).
        // Always the same failure message; does not reveal whether a password is set beyond 401/400.
        group.MapPost("/auth/verify-password", async (ClaimsPrincipal principal, AppDbContext db, VerifyPasswordRequest req) =>
        {
            const string genericFail = "Incorrect password.";

            var email = principal.Identity?.Name;
            if (string.IsNullOrEmpty(email))
                return Results.Unauthorized();

            if (string.IsNullOrEmpty(req.Password))
                return Results.BadRequest(new { success = false, message = genericFail });

            var user = await UserLookup.ByEmailAsync(db, email);
            if (user == null)
                return Results.Unauthorized();

            if (string.IsNullOrEmpty(user.PasswordHash))
            {
                PasswordHashService.DummyVerify(req.Password);
                return Results.Json(new { success = false, message = genericFail }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!PasswordHashService.Verify(req.Password, user.PasswordHash))
                return Results.Json(new { success = false, message = genericFail }, statusCode: StatusCodes.Status401Unauthorized);

            return Results.Ok(new { success = true });
        });

        group.MapGet("/auth/sessions", async (ClaimsPrincipal principal, AuthSessionService sessions) =>
        {
            var idVal = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(idVal, out var userId))
                return Results.Unauthorized();

            var list = await sessions.ListAsync(userId, AuthSessionService.ReadSid(principal));
            return Results.Ok(list);
        });

        group.MapPost("/auth/sessions/revoke", async (ClaimsPrincipal principal, AuthSessionService sessions, RevokeSessionRequest req) =>
        {
            var idVal = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(idVal, out var userId))
                return Results.Unauthorized();
            if (!Guid.TryParse(req.Id, out var rowId))
                return Results.BadRequest(new { message = "Unknown session." });

            await sessions.RevokeAsync(rowId, userId);
            return Results.Ok(new { success = true });
        });

        group.MapPost("/auth/sessions/revoke-others", async (ClaimsPrincipal principal, AuthSessionService sessions) =>
        {
            var idVal = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(idVal, out var userId))
                return Results.Unauthorized();

            await sessions.RevokeOthersAsync(userId, AuthSessionService.ReadSid(principal));
            return Results.Ok(new { success = true });
        });

        group.MapGet("/user/encryption-key", async (HttpContext ctx, ClaimsPrincipal user, KeyProtectionService protector, AppDbContext db, AuthSessionService sessions) =>
        {
            var email = user.Identity?.Name;
            if (string.IsNullOrEmpty(email))
                return Results.Unauthorized();

            var sid = AuthSessionService.ReadSid(user);
            var session = await sessions.FindValidAsync(sid);
            if (session != null && !AuthSessionService.DeviceMayUseSession(session, AuthSessionService.ReadDeviceId(ctx)))
            {
                return Results.Json(new
                {
                    code = "new_device",
                    message = "This device needs to sign in before it can sync."
                }, statusCode: StatusCodes.Status403Forbidden);
            }

            var u = await UserLookup.ByEmailAsync(db, email);
            if (u == null)
                return Results.Unauthorized();

            var plaintextKey = LocalEncryptionKeyService.ResolveStoredKey(u.LocalEncryptionKey, protector, out var migrateLegacy);

            if (plaintextKey == null && string.IsNullOrEmpty(u.LocalEncryptionKey))
            {
                plaintextKey = LocalEncryptionKeyService.GenerateRawKeyBase64();
                u.LocalEncryptionKey = plaintextKey;
                await db.SaveChangesAsync();
            }
            else if (plaintextKey == null)
            {
                return Results.Problem(
                    title: "Encryption key unavailable",
                    detail: "Your account encryption key could not be read. Data was not rotated. Contact support or restore homeserver.db from backup.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
            else if (migrateLegacy)
            {
                u.LocalEncryptionKey = plaintextKey;
                await db.SaveChangesAsync();
            }

            return Results.Ok(new { Key = plaintextKey });
        });

        group.MapGet("/keys", async (ProviderKeyService keyService, KeyProtectionService protector) =>
        {
            // Returns the user's server-side provider keys (decrypted on the server)
            // so an authenticated WASM client can import them for its own direct
            // browser-to-provider calls. The WASM will store its local copy encrypted
            // with the user's LocalEncryptionKey (fetched from the same DB).
            var keys = await keyService.GetUserKeysAsync();
            var result = keys.Select(k => new
            {
                k.ProviderId,
                k.Enabled,
                Key = string.IsNullOrEmpty(k.Key) ? null : protector.UnprotectOrPlain(k.Key)
            });
            return Results.Ok(result);
        });

        group.MapPost("/calendar/ics-fetch", async (CalendarIcsFetchRequest req) =>
        {
            var url = CalendarIcs.NormalizeFeedUrl(req.Url);
            if (!CalendarIcs.IsAllowedFeedUrl(url))
                return Results.BadRequest(new { message = "Enter an https (or webcal) calendar URL." });

            using var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "text/calendar, text/plain, */*");
            request.Headers.TryAddWithoutValidation("User-Agent", "Wizionic/1.0");
            if (!string.IsNullOrWhiteSpace(req.Etag))
                request.Headers.TryAddWithoutValidation("If-None-Match", req.Etag);
            if (!string.IsNullOrWhiteSpace(req.LastModified)
                && DateTimeOffset.TryParse(req.LastModified, out var lm))
                request.Headers.IfModifiedSince = lm;

            HttpResponseMessage resp;
            try
            {
                resp = await http.SendAsync(request);
            }
            catch (Exception ex)
            {
                return Results.Json(new { statusCode = 502, message = ex.Message }, statusCode: 502);
            }

            using (resp)
            {
                if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    return Results.Ok(new
                    {
                        statusCode = 304,
                        text = (string?)null,
                        etag = resp.Headers.ETag?.ToString(),
                        lastModified = resp.Content.Headers.LastModified?.ToString("R")
                    });
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length > 2 * 1024 * 1024)
                    return Results.Json(new { statusCode = 413, message = "Calendar is larger than 2 MB." }, statusCode: 413);

                var text = System.Text.Encoding.UTF8.GetString(bytes);
                return Results.Json(new
                {
                    statusCode = (int)resp.StatusCode,
                    text = resp.IsSuccessStatusCode ? text : null,
                    etag = resp.Headers.ETag?.ToString(),
                    lastModified = resp.Content.Headers.LastModified?.ToString("R"),
                    message = resp.IsSuccessStatusCode ? null : resp.ReasonPhrase
                }, statusCode: resp.IsSuccessStatusCode ? 200 : (int)resp.StatusCode);
            }
        });

        // Tool proxies for WASM agentic mode.
        // These allow the browser (WASM) to use web search and page summarization
        // without CORS errors (the browser would otherwise be blocked when calling
        // DuckDuckGo or Jina directly). The real HTTP work happens on the server
        // (using the exact same AppTools code as the interactive server chat).
        // Results flow back to the model through the normal tool-calling loop.
        // 
        toolsGroup.MapPost("/web-search", async (WebSearchRequest req) =>
        {
            return await App.Services.Tools.AppTools.SearchWeb(req.Query, req.MaxResults ?? 5);
        });

        toolsGroup.MapPost("/summarize-url", async (SummarizeUrlRequest req) =>
        {
            return await App.Services.Tools.AppTools.SummarizeUrl(req.Url);
        });

        toolsGroup.MapPost("/get-current-weather", async (WeatherRequest req) =>
        {
            return await App.Services.Tools.AppTools.GetCurrentWeather(req.Latitude, req.Longitude, req.Units ?? "celsius", req.ForecastDays ?? 0);
        });

        // Public MCP registry proxy + filter (official registry.modelcontextprotocol.io).
        // - Browse: version=latest, over-fetch until ~limit remote-capable servers (default 20).
        // - Search: passes search= upstream so the full registry is searched (not a client filter).
        // - Maps title, icons, publisher for GitHub-style cards. No SQLite catalog for browse results.
        // - Filters to streamable-http / sse / http only (web/WASM cannot launch stdio packages).
        toolsGroup.MapGet("/mcp-registry", async (HttpRequest httpRequest) =>
        {
            try
            {
                // Read query explicitly (q or search) — avoids subtle minimal-API binding quirks.
                var search = (httpRequest.Query["q"].FirstOrDefault()
                              ?? httpRequest.Query["search"].FirstOrDefault()
                              ?? "").Trim();
                var limitRaw = httpRequest.Query["limit"].FirstOrDefault();
                var want = 20;
                if (int.TryParse(limitRaw, out var parsedLimit))
                    want = Math.Clamp(parsedLimit, 1, 100);

                using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var jsonOpts = new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var results = new List<RemoteMcpServer>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string? cursor = null;
                // Over-fetch: many registry rows are stdio-only and get filtered out.
                var pageSize = string.IsNullOrEmpty(search)
                    ? Math.Min(100, Math.Max(want * 5, 50))
                    : Math.Min(100, Math.Max(want * 3, 40));
                var maxPages = string.IsNullOrEmpty(search) ? 6 : 8;

                for (var page = 0; page < maxPages && results.Count < want; page++)
                {
                    var url = BuildRegistryListUrl(search, pageSize, cursor);
                    var body = await FetchRegistryPageAsync(hc, url);
                    if (body is null)
                        break;

                    var raw = System.Text.Json.JsonSerializer.Deserialize<RegistryResponse>(body, jsonOpts);
                    if (raw?.Servers == null || raw.Servers.Count == 0)
                        break;

                    AppendMapped(raw, results, seen, want);
                    cursor = raw.Metadata?.NextCursor;
                    if (string.IsNullOrWhiteSpace(cursor))
                        break;
                }

                // Helpful for debugging whether the running host applied search.
                httpRequest.HttpContext.Response.Headers["X-Mcp-Registry-Q"] = search;
                httpRequest.HttpContext.Response.Headers["X-Mcp-Registry-Count"] = results.Count.ToString();
                return Results.Ok(results);
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, title: "Failed to fetch MCP registry");
            }

            static void AppendMapped(
                RegistryResponse raw,
                List<RemoteMcpServer> results,
                HashSet<string> seen,
                int want)
            {
                foreach (var entry in raw.Servers)
                {
                    if (results.Count >= want) break;
                    var mapped = MapRegistryEntry(entry);
                    if (mapped is null) continue;
                    if (!seen.Add(mapped.Name)) continue;
                    results.Add(mapped);
                }
            }
        });

        return endpoints;
    }

    // Request shapes for the tool proxy endpoints.
    public record WebSearchRequest(string Query, int? MaxResults = 5);
    public record SummarizeUrlRequest(string Url);
    public record WeatherRequest(double Latitude, double Longitude, string? Units = "celsius", int? ForecastDays = 0);

    /// <summary>
    /// Clears the optional login password and all 2FA fields. Does not touch LocalEncryptionKey.
    /// Returns true when anything actually changed.
    /// </summary>
    private static bool ClearPasswordAndTwoFactor(User user)
    {
        var changed = !string.IsNullOrEmpty(user.PasswordHash)
            || user.TwoFactorEnabled
            || !string.IsNullOrEmpty(user.TwoFactorPhoneE164)
            || !string.IsNullOrEmpty(user.TwoFactorChallengeHash)
            || user.TwoFactorChallengeExpiresAt != null
            || !string.IsNullOrEmpty(user.RecoveryCodesJson);

        if (!changed)
            return false;

        user.PasswordHash = null;
        user.TwoFactorEnabled = false;
        user.TwoFactorPhoneE164 = null;
        user.TwoFactorChallengeHash = null;
        user.TwoFactorChallengeExpiresAt = null;
        RecoveryCodeService.Clear(user);
        return true;
    }

    // Request for the public login flow (used by WASM and MAUI landing pages).
    public record CalendarIcsFetchRequest(string? Url, string? Etag = null, string? LastModified = null);

    public record RequestMagicLink(string Email);
    public record VerifyLoginCode(string Email, string Code);
    public record LoginWithPassword(string Email, string Password, bool RememberDevice = true);
    public record SetPasswordRequest(string? Password, string? ConfirmPassword, string? CurrentPassword = null);
    public record VerifyPasswordRequest(string? Password);
    public record TwoFactorSendRequest(string? ChallengeId, string? Method);
    public record TwoFactorVerifyRequest(string? ChallengeId, string? Code, string? Method, bool RememberDevice = true);
    public record RevokeSessionRequest(string? Id);
    public sealed class TwoFactorSettingsRequest
    {
        public bool Enabled { get; set; }
        public string? CurrentPassword { get; set; }
    }
    public record EnrollSmsRequest(string? Phone);
    public record ConfirmSmsRequest(string? Phone, string? Code);
    public record RemovePhoneRequest(string? CurrentPassword);

    // --- MCP Registry proxy models (clean output for the Tools page) ---

    /// <summary>
    /// Clean, filtered representation of a remote-capable MCP server for the Tools UI.
    /// Only entries that have usable HTTP/SSE remotes are returned (stdio-only servers are dropped server-side).
    /// Browse/search metadata is never persisted — only install-time config lives in the client KeyStore.
    /// </summary>
    public record RemoteMcpServer(
        string Name,
        string Description,
        string RemoteUrl,
        string Transport,
        bool RequiresAuth,
        string? InfoUrl,
        string Version,
        string? Title = null,
        string? Publisher = null,
        string? IconUrl = null,
        DateTimeOffset? UpdatedAt = null
    );

    // --- Raw registry deserialization shapes (match https://registry.modelcontextprotocol.io/v0/servers) ---

    public class RegistryResponse
    {
        public List<RegistryEntry> Servers { get; set; } = new();
        public RegistryListMetadata? Metadata { get; set; }
    }

    public class RegistryListMetadata
    {
        public string? NextCursor { get; set; }
        public int? Count { get; set; }
    }

    public class RegistryEntry
    {
        public RegistryServer? Server { get; set; }

        [JsonPropertyName("_meta")]
        public RegistryMeta? _meta { get; set; }
    }

    public class RegistryServer
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? Version { get; set; }
        public string? WebsiteUrl { get; set; }

        public RegistryRepository? Repository { get; set; }
        public List<RegistryRemote>? Remotes { get; set; }
        public List<RegistryIcon>? Icons { get; set; }
        // packages (stdio etc.) are intentionally ignored for the web client list
    }

    public class RegistryRepository
    {
        public string? Url { get; set; }
        public string? Source { get; set; }
    }

    public class RegistryIcon
    {
        public string? Src { get; set; }
        public string? MimeType { get; set; }
        public string? Theme { get; set; }
    }

    public class RegistryRemote
    {
        public string Type { get; set; } = string.Empty;
        public string? Url { get; set; }

        public List<RegistryHeader>? Headers { get; set; }
    }

    public class RegistryHeader
    {
        public string Name { get; set; } = string.Empty;
        public bool IsRequired { get; set; }
        public bool IsSecret { get; set; }
    }

    public class RegistryMeta
    {
        [JsonPropertyName("io.modelcontextprotocol.registry/official")]
        public OfficialMeta? Official { get; set; }
    }

    public class OfficialMeta
    {
        public bool IsLatest { get; set; }
        public string? Status { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
    }

    private static string BuildRegistryListUrl(string search, int pageSize, string? cursor)
    {
        // Prefer v0.1 (API freeze); v0 remains compatible for the same list shape.
        var qs = new List<string>
        {
            $"limit={pageSize}",
            "version=latest"
        };
        if (!string.IsNullOrEmpty(search))
            qs.Add("search=" + Uri.EscapeDataString(search));
        if (!string.IsNullOrWhiteSpace(cursor))
            qs.Add("cursor=" + Uri.EscapeDataString(cursor));

        return "https://registry.modelcontextprotocol.io/v0.1/servers?" + string.Join("&", qs);
    }

    private static async Task<string?> FetchRegistryPageAsync(HttpClient hc, string v01Url)
    {
        try
        {
            using var resp = await hc.GetAsync(v01Url);
            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadAsStringAsync();
        }
        catch
        {
            // try v0 below
        }

        try
        {
            var v0Url = v01Url.Replace("/v0.1/", "/v0/", StringComparison.Ordinal);
            using var resp = await hc.GetAsync(v0Url);
            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadAsStringAsync();
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static RemoteMcpServer? MapRegistryEntry(RegistryEntry? entry)
    {
        var sv = entry?.Server;
        if (sv is null || string.IsNullOrWhiteSpace(sv.Name))
            return null;

        // Prefer active/latest; still allow unknown status.
        var official = entry?._meta?.Official;
        if (official is { Status: not null } &&
            !string.Equals(official.Status, "active", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(official.Status, "deprecated", StringComparison.OrdinalIgnoreCase))
        {
            // skip deleted / disabled
            if (string.Equals(official.Status, "deleted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(official.Status, "archived", StringComparison.OrdinalIgnoreCase))
                return null;
        }

        var remote = sv.Remotes?.FirstOrDefault(r =>
            !string.IsNullOrWhiteSpace(r?.Url) &&
            (string.Equals(r.Type, "streamable-http", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(r.Type, "sse", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(r.Type, "http", StringComparison.OrdinalIgnoreCase)));

        if (remote == null)
            return null;

        bool requiresAuth = remote.Headers?.Any(h => h is { IsRequired: true }) ?? false;

        string? infoUrl = !string.IsNullOrWhiteSpace(sv.WebsiteUrl) ? sv.WebsiteUrl :
                          sv.Repository?.Url;

        var title = !string.IsNullOrWhiteSpace(sv.Title)
            ? sv.Title!
            : ShortRegistryTitle(sv.Name);

        return new RemoteMcpServer(
            Name: sv.Name,
            Description: sv.Description ?? sv.Title ?? "",
            RemoteUrl: remote.Url!,
            Transport: remote.Type ?? "remote",
            RequiresAuth: requiresAuth,
            InfoUrl: infoUrl,
            Version: sv.Version ?? "",
            Title: title,
            Publisher: DerivePublisher(sv.Name, sv.Repository?.Url),
            IconUrl: ResolveIconUrl(sv.Icons, sv.Repository?.Url, sv.WebsiteUrl, remote.Url),
            UpdatedAt: official?.UpdatedAt ?? official?.PublishedAt
        );
    }

    private static string ShortRegistryTitle(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "MCP";
        var last = fullName.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? fullName;
        return last.Length > 48 ? last[..45] + "…" : last;
    }

    /// <summary>
    /// "By …" publisher: reverse-DNS namespace before '/', else GitHub owner from repo URL.
    /// </summary>
    private static string? DerivePublisher(string name, string? repositoryUrl)
    {
        if (!string.IsNullOrWhiteSpace(name) && name.Contains('/'))
        {
            var ns = name.Split('/', 2)[0].Trim();
            if (!string.IsNullOrEmpty(ns))
            {
                // com.github.user → user; io.modelcontextprotocol → modelcontextprotocol
                var parts = ns.Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    // Prefer last meaningful label (skip common TLDs/prefixes when multi-part)
                    var last = parts[^1];
                    if (parts.Length >= 3 && parts[0] is "com" or "io" or "org" or "ai" or "net")
                        return parts[^1];
                    if (parts.Length == 2)
                        return parts[1]; // ai.smithery → smithery
                    return last;
                }
                return ns;
            }
        }

        if (!string.IsNullOrWhiteSpace(repositoryUrl) &&
            Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri) &&
            uri.Host.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segs = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length >= 1)
                return segs[0];
        }

        return null;
    }

    private static string? ResolveIconUrl(
        List<RegistryIcon>? icons,
        string? repositoryUrl,
        string? websiteUrl,
        string? remoteUrl)
    {
        if (icons != null)
        {
            foreach (var icon in icons)
            {
                var src = icon?.Src?.Trim();
                if (string.IsNullOrEmpty(src)) continue;
                if (src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return src;
            }
        }

        // GitHub org/user avatar fallback (no local icon storage).
        if (!string.IsNullOrWhiteSpace(repositoryUrl) &&
            Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repoUri) &&
            repoUri.Host.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segs = repoUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length >= 1)
                return $"https://github.com/{segs[0]}.png?size=64";
        }

        // Website / remote host favicon via Google s2 (HTTPS, no storage).
        var hostCandidate = FirstHttpHost(websiteUrl) ?? FirstHttpHost(remoteUrl) ?? FirstHttpHost(repositoryUrl);
        if (!string.IsNullOrEmpty(hostCandidate))
            return $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(hostCandidate)}&sz=64";

        return null;
    }

    private static string? FirstHttpHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        if (string.IsNullOrWhiteSpace(uri.Host)) return null;
        // Skip placeholder hosts in templated MCP URLs
        if (uri.Host.Contains('{') || uri.Host.Contains('}')) return null;
        return uri.Host;
    }
}
