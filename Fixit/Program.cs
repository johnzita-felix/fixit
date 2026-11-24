using System.Net;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using System.Xml;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

var databasePath = Path.Combine(builder.Environment.ContentRootPath, "app.db");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddHostedService<ResolvedReportCleanupService>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "fixIt.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(2);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = async context =>
        {
            var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var sessionVersion = context.Principal?.FindFirstValue("fixIt:SessionVersion");
            if (!int.TryParse(userId, out var parsedUserId))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }
            var parsedSessionVersion = int.TryParse(sessionVersion, out var version) ? version : 0;

            var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            var currentSessionVersion = await db.Users
                .Where(user => user.Id == parsedUserId)
                .Select(user => (int?)user.SessionVersion)
                .SingleOrDefaultAsync();
            if (currentSessionVersion != parsedSessionVersion)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("password-reset-request", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(15),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();

// Create new databases and safely add auth columns to existing Users tables.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    await EnsureAuthColumnsAsync(db);
    await EnsureReportSchemaAsync(db);
    await EnsureSchoolSchemaAsync(db);
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/api/register", async (
    RegisterRequest request,
    AppDbContext db,
    IPasswordHasher<AppUser> hasher,
    IConfiguration config,
    CancellationToken cancellationToken) =>
{
    var username = NormalizePersonName(request.Username);
    var email = NormalizeEmail(request.Email);
    var password = request.Password;

    if (string.IsNullOrWhiteSpace(username) ||
        string.IsNullOrWhiteSpace(email) ||
        string.IsNullOrWhiteSpace(password))
        return Results.BadRequest(new { message = "Name, email, and password are required." });

    if (username is null || !IsValidPersonName(username))
        return Results.BadRequest(new { message = "Name may contain letters, spaces, hyphens, and apostrophes only." });
    if (username.Length < 3 || username.Length > 50)
        return Results.BadRequest(new { message = "Name must contain 3–50 characters." });

    if (!IsValidEmail(email))
        return Results.BadRequest(new { message = "Enter a valid email address." });

    if (password.Length < 8 || password.Length > 128)
        return Results.BadRequest(new { message = "Password must contain 8–128 characters." });

    if (password != request.ConfirmPassword)
        return Results.BadRequest(new { message = "Passwords do not match." });

    var smtp = SmtpSettings.FromConfiguration(config);
    if (!smtp.IsConfigured)
    {
        return Results.Problem(
            title: "Email verification is not configured",
            detail: "The server administrator must configure SMTP email settings before registration can be completed.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var hasClassChoice = request.GradeLevelId is not null || request.SectionId is not null;
    if (hasClassChoice &&
        (request.GradeLevelId is null ||
         request.SectionId is null ||
         !await db.SchoolSections.AnyAsync(section =>
             section.Id == request.SectionId && section.GradeLevelId == request.GradeLevelId && section.IsActive, cancellationToken) ||
         !await db.SchoolGrades.AnyAsync(grade =>
             grade.Id == request.GradeLevelId && grade.IsActive, cancellationToken)))
        return Results.BadRequest(new { message = "Choose a valid grade level and section from the school directory." });

    if (await db.Users.AnyAsync(u => u.Username == username, cancellationToken))
        return Results.Conflict(new { message = "That name is already registered. Try a different name." });

    if (await db.Users.AnyAsync(u => u.Email != null && u.Email.ToLower() == email, cancellationToken))
        return Results.Conflict(new { message = "An account with that email already exists." });

    var pending = await db.PendingRegistrations.SingleOrDefaultAsync(
        item => item.Email == email, cancellationToken);
    if (pending is not null && pending.CreatedUtc.AddSeconds(60) > DateTime.UtcNow)
    {
        var retryAfter = Math.Max(1, (int)Math.Ceiling((pending.CreatedUtc.AddSeconds(60) - DateTime.UtcNow).TotalSeconds));
        return Results.Json(new
        {
            message = "A code is already active for this email. Verify the current registration or wait before starting again.",
            expiresInSeconds = Math.Max(0, (int)Math.Ceiling((pending.ExpiresUtc - DateTime.UtcNow).TotalSeconds)),
            resendAfterSeconds = retryAfter
        }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    if (await db.PendingRegistrations.AnyAsync(
            item => item.Email != email && item.Username == username, cancellationToken))
        return Results.Conflict(new { message = "That name is being used by another pending registration." });

    pending ??= new PendingRegistration { Email = email };
    var passwordUser = new AppUser { Username = username, Email = email };
    pending.Username = username;
    pending.PasswordHash = hasher.HashPassword(passwordUser, password);
    pending.GradeLevelId = request.GradeLevelId;
    pending.SectionId = request.SectionId;
    var code = SetRegistrationCode(pending, DateTime.UtcNow);
    if (db.Entry(pending).State == EntityState.Detached)
        db.PendingRegistrations.Add(pending);

    try
    {
        await db.SaveChangesAsync(cancellationToken);
    }
    catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqlite &&
                                        (sqlite.SqliteExtendedErrorCode == 2067 ||
                                         sqlite.SqliteExtendedErrorCode == 1555))
    {
        return Results.Conflict(new { message = "That name or email is already being used by another registration." });
    }

    try
    {
        await SendRegistrationVerificationCodeEmailAsync(smtp, email, code, cancellationToken);
    }
    catch (SmtpException)
    {
        InvalidateRegistrationCode(pending);
        pending.CreatedUtc = DateTime.UtcNow.AddSeconds(-60);
        await db.SaveChangesAsync(cancellationToken);
        app.Logger.LogError("Failed to deliver a registration verification email.");
        return Results.Problem(
            title: "Unable to send verification code",
            detail: "Email delivery failed. Please try again later.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (FormatException)
    {
        InvalidateRegistrationCode(pending);
        pending.CreatedUtc = DateTime.UtcNow.AddSeconds(-60);
        await db.SaveChangesAsync(cancellationToken);
        app.Logger.LogError("Registration email delivery is misconfigured.");
        return Results.Problem(
            title: "Unable to send verification code",
            detail: "Email delivery failed. Please contact support.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new
    {
        message = "A six-digit verification code was sent to your email address.",
        expiresInSeconds = 600,
        resendAfterSeconds = 60
    });
}).RequireRateLimiting("auth");

app.MapPost("/api/register/resend-code", async (
    RegistrationEmailRequest request,
    AppDbContext db,
    IConfiguration config,
    CancellationToken cancellationToken) =>
{
    var email = NormalizeEmail(request.Email);
    if (string.IsNullOrWhiteSpace(email) || !IsValidEmail(email))
        return Results.BadRequest(new { message = "Enter a valid email address." });

    var pending = await db.PendingRegistrations.SingleOrDefaultAsync(
        item => item.Email == email, cancellationToken);
    if (pending is null)
        return Results.BadRequest(new { message = "No pending registration was found. Return to the registration form and try again." });

    var now = DateTime.UtcNow;
    var retryAt = pending.CreatedUtc.AddSeconds(60);
    if (retryAt > now)
    {
        var retryAfter = Math.Max(1, (int)Math.Ceiling((retryAt - now).TotalSeconds));
        return Results.Json(new
        {
            message = $"Please wait {retryAfter} seconds before requesting another code.",
            resendAfterSeconds = retryAfter
        }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    var smtp = SmtpSettings.FromConfiguration(config);
    if (!smtp.IsConfigured)
    {
        return Results.Problem(
            title: "Email verification is not configured",
            detail: "The server administrator must configure SMTP email settings before registration can be completed.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var code = SetRegistrationCode(pending, now);
    await db.SaveChangesAsync(cancellationToken);

    try
    {
        await SendRegistrationVerificationCodeEmailAsync(smtp, email, code, cancellationToken);
    }
    catch (SmtpException)
    {
        InvalidateRegistrationCode(pending);
        pending.CreatedUtc = DateTime.UtcNow.AddSeconds(-60);
        await db.SaveChangesAsync(cancellationToken);
        app.Logger.LogError("Failed to deliver a resent registration verification email.");
        return Results.Problem(
            title: "Unable to send verification code",
            detail: "Email delivery failed. Please try again later.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (FormatException)
    {
        InvalidateRegistrationCode(pending);
        pending.CreatedUtc = DateTime.UtcNow.AddSeconds(-60);
        await db.SaveChangesAsync(cancellationToken);
        app.Logger.LogError("Registration email delivery is misconfigured.");
        return Results.Problem(
            title: "Unable to send verification code",
            detail: "Email delivery failed. Please contact support.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new
    {
        message = "A new verification code was sent.",
        expiresInSeconds = 600,
        resendAfterSeconds = 60
    });
}).RequireRateLimiting("auth");

app.MapPost("/api/register/verify", async (
    VerifyRegistrationRequest request,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var email = NormalizeEmail(request.Email);
    if (string.IsNullOrWhiteSpace(email) || !IsValidEmail(email) ||
        request.Code is null || request.Code.Length != 6 ||
        request.Code.Any(character => character is < '0' or > '9'))
        return Results.BadRequest(new { message = "Enter the six-digit verification code sent to your email." });

    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    var pending = await db.PendingRegistrations.SingleOrDefaultAsync(
        item => item.Email == email, cancellationToken);
    if (pending is null)
        return Results.BadRequest(new { message = "That verification code is invalid or the registration is no longer available. Start again." });

    if (pending.CodeHash is null || pending.CodeSalt is null)
        return Results.BadRequest(new { message = "That verification code is invalid or has expired. Request a new code." });

    if (pending.Attempts >= 5)
        return Results.BadRequest(new { message = "Too many incorrect attempts. Request a new code." });

    if (pending.ExpiresUtc <= DateTime.UtcNow)
    {
        InvalidateRegistrationCode(pending);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.BadRequest(new { message = "That verification code has expired. Request a new code." });
    }

    if (!CryptographicOperations.FixedTimeEquals(
            pending.CodeHash,
            HashVerificationCode(request.Code, pending.CodeSalt)))
    {
        pending.Attempts++;
        if (pending.Attempts >= 5)
            InvalidateRegistrationCode(pending);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.BadRequest(new
        {
            message = pending.Attempts >= 5
                ? "Too many incorrect attempts. Request a new code."
                : "That verification code is invalid. Check it and try again."
        });
    }

    if (await db.Users.AnyAsync(
            user => user.Email != null && user.Email.ToLower() == email, cancellationToken) ||
        await db.Users.AnyAsync(user => user.Username == pending.Username, cancellationToken))
    {
        db.PendingRegistrations.Remove(pending);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Conflict(new { message = "That name or email is already registered." });
    }

    var user = new AppUser
    {
        Username = pending.Username,
        Email = pending.Email,
        PasswordHash = pending.PasswordHash,
        GradeLevelId = pending.GradeLevelId,
        SectionId = pending.SectionId,
        Role = "user"
    };
    db.Users.Add(user);
    db.PendingRegistrations.Remove(pending);

    try
    {
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqlite &&
                                        (sqlite.SqliteExtendedErrorCode == 2067 ||
                                         sqlite.SqliteExtendedErrorCode == 1555))
    {
        await transaction.RollbackAsync(cancellationToken);
        return Results.Conflict(new { message = "That name or email is already registered." });
    }

    return Results.Ok(new { message = "Email verified and account created. You can now sign in." });
}).RequireRateLimiting("auth");

app.MapPost("/api/login", async (
    LoginRequest request,
    AppDbContext db,
    IPasswordHasher<AppUser> hasher,
    HttpContext context) =>
{
    var email = NormalizeEmail(request.Email);
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { message = "Enter your email and password." });

    var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
    if (user is null)
        return Results.Json(new { message = "Invalid email or password." }, statusCode: StatusCodes.Status401Unauthorized);

    var result = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
    if (result == PasswordVerificationResult.Failed)
        return Results.Json(new { message = "Invalid email or password." }, statusCode: StatusCodes.Status401Unauthorized);

    if (result == PasswordVerificationResult.SuccessRehashNeeded)
    {
        user.PasswordHash = hasher.HashPassword(user, request.Password!);
        await db.SaveChangesAsync();
    }

    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.Email, user.Email ?? ""),
        new Claim(ClaimTypes.Role, user.Role == "admin" ? "admin" : "user"),
        new Claim("fixIt:SessionVersion", user.SessionVersion.ToString())
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));

    var redirectUrl = user.Role == "admin" ? "dashboard_admin.html" : "dashboard_user.html";
    return Results.Ok(new { message = "Login successful!", redirectUrl });
}).RequireRateLimiting("auth");

app.MapPost("/api/forgot-password", async (
    ForgotPasswordRequest request,
    AppDbContext db,
    IConfiguration config) =>
{
    var email = NormalizeEmail(request.Email);
    if (string.IsNullOrWhiteSpace(email) || !IsValidEmail(email))
        return Results.BadRequest(new { message = "Enter a valid email address." });

    var smtp = SmtpSettings.FromConfiguration(config);
    if (!smtp.IsConfigured)
    {
        return Results.Problem(
            title: "Email recovery is not configured",
            detail: "The server administrator must configure SMTP email settings before password recovery can be used.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    const string genericMessage = "If an account with that email exists, a verification code will be sent shortly.";
    const int codeLifetimeSeconds = 600;
    var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
    if (user is null)
        return Results.Ok(new { message = genericMessage, expiresInSeconds = codeLifetimeSeconds });

    var now = DateTime.UtcNow;
    var resetCode = await db.PasswordResetCodes.SingleOrDefaultAsync(item => item.Email == email);
    if (resetCode is not null && resetCode.CreatedUtc.AddSeconds(60) > now)
        return Results.Ok(new { message = genericMessage, expiresInSeconds = codeLifetimeSeconds });

    var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    var salt = RandomNumberGenerator.GetBytes(16);
    if (resetCode is null)
    {
        resetCode = new PasswordResetCode { Email = email };
        db.PasswordResetCodes.Add(resetCode);
    }

    resetCode.CodeHash = HashVerificationCode(code, salt);
    resetCode.CodeSalt = salt;
    resetCode.ExpiresUtc = now.AddMinutes(10);
    resetCode.CreatedUtc = now;
    resetCode.Attempts = 0;
    resetCode.ResetGrantHash = null;
    resetCode.ResetGrantExpiresUtc = null;
    resetCode.UsedUtc = null;
    user.PasswordResetTokenHash = null;
    user.PasswordResetTokenExpiresUtc = null;
    await db.SaveChangesAsync();

    try
    {
        await SendVerificationCodeEmailAsync(smtp, email, code);
        return Results.Ok(new { message = genericMessage, expiresInSeconds = codeLifetimeSeconds });
    }
    catch (SmtpException)
    {
        resetCode.CodeHash = null;
        resetCode.CodeSalt = null;
        resetCode.UsedUtc = DateTime.UtcNow;
        resetCode.CreatedUtc = DateTime.UtcNow.AddSeconds(-60);
        await db.SaveChangesAsync();
        app.Logger.LogError("Failed to deliver a password recovery verification email.");
        return Results.Problem(
            title: "Unable to send verification code",
            detail: "Email delivery failed. Please try again later or contact support.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireRateLimiting("auth").RequireRateLimiting("password-reset-request");

app.MapPost("/api/verify-reset-code", async (
    VerifyResetCodeRequest request,
    AppDbContext db) =>
{
    var email = NormalizeEmail(request.Email);
    if (string.IsNullOrWhiteSpace(email) || !IsValidEmail(email) ||
        request.Code is null || request.Code.Length != 6 ||
        request.Code.Any(character => character is < '0' or > '9'))
        return Results.BadRequest(new { message = "Enter the six-digit verification code sent to your email." });

    await using var transaction = await db.Database.BeginTransactionAsync();
    var resetCode = await db.PasswordResetCodes.SingleOrDefaultAsync(item => item.Email == email);
    if (resetCode is null)
        return Results.BadRequest(new { message = "That verification code is invalid. Check it and try again." });

    if (resetCode.UsedUtc is not null)
        return Results.BadRequest(new { message = "That verification code has already been used. Request a new code." });

    if (resetCode.Attempts >= 5)
        return Results.BadRequest(new { message = "Too many incorrect attempts. Request a new code." });

    if (resetCode.CodeHash is null)
        return Results.BadRequest(new { message = "That verification code has already been verified or is no longer valid. Request a new code." });

    if (resetCode.ExpiresUtc <= DateTime.UtcNow)
        return Results.BadRequest(new { message = "That verification code has expired. Request a new code." });

    if (!CryptographicOperations.FixedTimeEquals(
            resetCode.CodeHash,
            HashVerificationCode(request.Code, resetCode.CodeSalt!)))
    {
        resetCode.Attempts++;
        if (resetCode.Attempts >= 5)
        {
            resetCode.CodeHash = null;
            resetCode.CodeSalt = null;
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return Results.BadRequest(new
        {
            message = resetCode.Attempts >= 5
                ? "Too many incorrect attempts. Request a new code."
                : "That verification code is invalid. Check it and try again."
        });
    }

    var resetToken = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
        RandomNumberGenerator.GetBytes(32));
    resetCode.CodeHash = null;
    resetCode.CodeSalt = null;
    resetCode.ResetGrantHash = SHA256.HashData(Encoding.UTF8.GetBytes(resetToken));
    resetCode.ResetGrantExpiresUtc = DateTime.UtcNow.AddMinutes(10);
    await db.SaveChangesAsync();
    await transaction.CommitAsync();

    return Results.Ok(new
    {
        message = "Email verified. You can now choose a new password.",
        resetToken
    });
}).RequireRateLimiting("auth");

app.MapPost("/api/reset-password", async (
    ResetPasswordRequest request,
    AppDbContext db,
    IPasswordHasher<AppUser> hasher) =>
{
    if (string.IsNullOrWhiteSpace(request.ResetToken))
        return Results.BadRequest(new { message = "Verify your email address before changing your password." });

    if (string.IsNullOrEmpty(request.Password) || request.Password.Length < 8 || request.Password.Length > 128)
        return Results.BadRequest(new { message = "Password must contain 8–128 characters." });

    if (request.Password != request.ConfirmPassword)
        return Results.BadRequest(new { message = "Passwords do not match." });

    var grantHash = SHA256.HashData(Encoding.UTF8.GetBytes(request.ResetToken));
    await using var transaction = await db.Database.BeginTransactionAsync();
    var resetCode = await db.PasswordResetCodes.SingleOrDefaultAsync(item =>
        item.ResetGrantHash == grantHash);
    if (resetCode is null || resetCode.UsedUtc is not null)
        return Results.BadRequest(new { message = "This password reset authorization is invalid or has already been used. Verify your email again." });

    if (resetCode.ResetGrantExpiresUtc is null ||
        resetCode.ResetGrantExpiresUtc <= DateTime.UtcNow)
        return Results.BadRequest(new { message = "Your verified session has expired. Request a new verification code." });

    var user = await db.Users.SingleOrDefaultAsync(item => item.Email == resetCode.Email);
    if (user is null)
        return Results.BadRequest(new { message = "This password reset authorization is invalid or has already been used. Verify your email again." });

    user.PasswordHash = hasher.HashPassword(user, request.Password);
    user.PasswordResetTokenHash = null;
    user.PasswordResetTokenExpiresUtc = null;
    user.SessionVersion++;
    resetCode.ResetGrantHash = null;
    resetCode.ResetGrantExpiresUtc = null;
    resetCode.UsedUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();
    await transaction.CommitAsync();

    return Results.Ok(new { message = "Password updated successfully. You can now sign in." });
}).RequireRateLimiting("auth");

app.MapGet("/api/me", async (HttpContext context, AppDbContext db) =>
    Results.Ok(new
    {
        username = context.User.Identity?.Name,
        email = context.User.FindFirstValue(ClaimTypes.Email),
        role = context.User.FindFirstValue(ClaimTypes.Role) ?? "user",
        profile = await db.Users.AsNoTracking()
            .Where(user => user.Id == int.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!))
            .Select(user => new
            {
                user.GradeLevelId,
                user.SectionId
            }).SingleAsync()
    })).RequireAuthorization();

app.MapPost("/api/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { message = "Logged out." });
}).RequireAuthorization();

app.MapGet("/api/classes", async (AppDbContext db) =>
{
    var grades = await GetClassDirectoryAsync(db);
    return Results.Ok(new { grades });
});

app.MapGet("/api/admin/class-options", async (AppDbContext db) =>
{
    var grades = await db.SchoolGrades.AsNoTracking()
        .OrderBy(grade => grade.SortOrder).ThenBy(grade => grade.Name)
        .Select(grade => new AdminGradeDirectoryResponse(
            grade.Id, grade.Name, grade.IsActive, new List<AdminSectionDirectoryResponse>()))
        .ToListAsync();
    var sections = await db.SchoolSections.AsNoTracking()
        .OrderBy(section => section.SortOrder).ThenBy(section => section.Name)
        .Select(section => new AdminSectionDirectoryResponse(
            section.Id, section.Name, section.GradeLevelId, section.IsActive))
        .ToListAsync();
    var gradeById = grades.ToDictionary(grade => grade.Id);
    foreach (var section in sections)
        if (gradeById.TryGetValue(section.GradeLevelId, out var grade))
            grade.Sections.Add(section);
    return Results.Ok(new { grades });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/admin/class-options/grades", async (
    CreateClassFolderRequest request,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var name = NormalizeClassFolderName(request.Name, 40);
    if (name is null)
        return Results.BadRequest(new { message = "Grade level names are required and limited to 40 characters." });
    var gradeNames = await db.SchoolGrades.AsNoTracking().Select(grade => grade.Name).ToListAsync(cancellationToken);
    if (gradeNames.Any(existing => ClassNameKey(existing) == ClassNameKey(name)))
        return Results.Conflict(new { message = "A grade level with that name already exists." });

    var grade = new SchoolGrade
    {
        Name = name,
        SortOrder = await db.SchoolGrades.CountAsync(cancellationToken) + 1
    };
    db.SchoolGrades.Add(grade);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new { id = grade.Id, name = grade.Name, message = "Grade level created." });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPut("/api/admin/class-options/grades/{gradeLevelId:int}", async (
    int gradeLevelId,
    RenameClassFolderRequest request,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var grade = await db.SchoolGrades.SingleOrDefaultAsync(item => item.Id == gradeLevelId, cancellationToken);
    if (grade is null) return Results.NotFound(new { message = "Grade level not found." });
    var name = NormalizeClassFolderName(request.Name, 40);
    if (name is null)
        return Results.BadRequest(new { message = "Grade level names are required and limited to 40 characters." });
    var otherGradeNames = await db.SchoolGrades.AsNoTracking()
        .Where(item => item.Id != gradeLevelId).Select(item => item.Name).ToListAsync(cancellationToken);
    if (otherGradeNames.Any(existing => ClassNameKey(existing) == ClassNameKey(name)))
        return Results.Conflict(new { message = "A grade level with that name already exists." });
    grade.Name = name;
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new { id = grade.Id, name = grade.Name, message = "Grade level renamed." });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPatch("/api/admin/class-options/grades/{gradeLevelId:int}/status", async (
    int gradeLevelId,
    UpdateClassFolderStatusRequest request,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var grade = await db.SchoolGrades.SingleOrDefaultAsync(item => item.Id == gradeLevelId, cancellationToken);
    if (grade is null) return Results.NotFound(new { message = "Grade level not found." });
    if (request.IsActive is not bool isActive)
        return Results.BadRequest(new { message = "Choose whether to archive or restore the grade level." });
    grade.IsActive = isActive;
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new { message = isActive ? "Grade level restored." : "Grade level archived." });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/admin/class-options/grades/{gradeLevelId:int}/deletion-check", async (
    int gradeLevelId,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var grade = await db.SchoolGrades.AsNoTracking()
        .SingleOrDefaultAsync(item => item.Id == gradeLevelId, cancellationToken);
    if (grade is null) return Results.NotFound(new { message = "Grade level not found." });
    var sectionIds = db.SchoolSections.Where(section => section.GradeLevelId == gradeLevelId)
        .Select(section => section.Id);
    var sectionCount = await db.SchoolSections.CountAsync(
        section => section.GradeLevelId == gradeLevelId, cancellationToken);
    var studentCount = await db.Users.CountAsync(user =>
        user.GradeLevelId == gradeLevelId || (user.SectionId != null && sectionIds.Contains(user.SectionId.Value)),
        cancellationToken);
    var reportCount = await db.Reports.CountAsync(report => db.Users.Any(user =>
        user.Id == report.UserId &&
        (user.GradeLevelId == gradeLevelId || (user.SectionId != null && sectionIds.Contains(user.SectionId.Value)))),
        cancellationToken);
    return Results.Ok(new
    {
        canDelete = sectionCount == 0 && studentCount == 0 && reportCount == 0,
        sectionCount,
        studentCount,
        reportCount
    });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapDelete("/api/admin/class-options/grades/{gradeLevelId:int}", async (
    int gradeLevelId,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    var grade = await db.SchoolGrades.SingleOrDefaultAsync(item => item.Id == gradeLevelId, cancellationToken);
    if (grade is null) return Results.NotFound(new { message = "Grade level not found." });
    var sectionIds = db.SchoolSections.Where(section => section.GradeLevelId == gradeLevelId)
        .Select(section => section.Id);
    var hasSections = await db.SchoolSections.AnyAsync(
        section => section.GradeLevelId == gradeLevelId, cancellationToken);
    var hasStudents = await db.Users.AnyAsync(user =>
        user.GradeLevelId == gradeLevelId || (user.SectionId != null && sectionIds.Contains(user.SectionId.Value)),
        cancellationToken);
    var hasReports = await db.Reports.AnyAsync(report => db.Users.Any(user =>
        user.Id == report.UserId &&
        (user.GradeLevelId == gradeLevelId || (user.SectionId != null && sectionIds.Contains(user.SectionId.Value)))),
        cancellationToken);
    if (hasSections || hasStudents || hasReports)
        return Results.Conflict(new
        {
            message = "This grade level cannot be permanently deleted because it contains sections or has student/report history. Archive it instead.",
            hasSections,
            hasStudents,
            hasReports
        });

    db.SchoolGrades.Remove(grade);
    try
    {
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (DbUpdateException exception) when (exception.InnerException is SqliteException
        { SqliteExtendedErrorCode: 787 })
    {
        return Results.Conflict(new { message = "This grade level is still referenced by existing records. Archive it instead." });
    }
    return Results.Ok(new { message = "Grade level permanently deleted." });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/admin/class-options/grades/{gradeLevelId:int}/sections", async (
    int gradeLevelId,
    CreateClassFolderRequest request,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var grade = await db.SchoolGrades.SingleOrDefaultAsync(item => item.Id == gradeLevelId, cancellationToken);
    if (grade is null) return Results.NotFound(new { message = "Grade level not found." });
    if (!grade.IsActive)
        return Results.Conflict(new { message = "Restore the grade level before adding sections." });
    var name = NormalizeClassFolderName(request.Name, 80);
    if (name is null)
        return Results.BadRequest(new { message = "Section names are required and limited to 80 characters." });
    var sectionNames = await db.SchoolSections.AsNoTracking()
        .Where(section => section.GradeLevelId == gradeLevelId)
        .Select(section => section.Name).ToListAsync(cancellationToken);
    if (sectionNames.Any(existing => ClassNameKey(existing) == ClassNameKey(name)))
        return Results.Conflict(new { message = "A section with that name already exists in this grade level." });

    var section = new SchoolSection
    {
        GradeLevelId = gradeLevelId,
        Name = name,
        SortOrder = await db.SchoolSections.CountAsync(
            item => item.GradeLevelId == gradeLevelId, cancellationToken) + 1
    };
    db.SchoolSections.Add(section);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new { id = section.Id, name = section.Name, message = "Section created." });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPut("/api/admin/class-options/sections/{sectionId:int}", async (
    int sectionId,
    RenameClassFolderRequest request,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var section = await db.SchoolSections.SingleOrDefaultAsync(item => item.Id == sectionId, cancellationToken);
    if (section is null) return Results.NotFound(new { message = "Section not found." });
    var name = NormalizeClassFolderName(request.Name, 80);
    if (name is null)
        return Results.BadRequest(new { message = "Section names are required and limited to 80 characters." });
    var otherSectionNames = await db.SchoolSections.AsNoTracking()
        .Where(item => item.Id != sectionId && item.GradeLevelId == section.GradeLevelId)
        .Select(item => item.Name).ToListAsync(cancellationToken);
    if (otherSectionNames.Any(existing => ClassNameKey(existing) == ClassNameKey(name)))
        return Results.Conflict(new { message = "A section with that name already exists in this grade level." });
    section.Name = name;
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new { id = section.Id, name = section.Name, message = "Section renamed." });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPatch("/api/admin/class-options/sections/{sectionId:int}/status", async (
    int sectionId,
    UpdateClassFolderStatusRequest request,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var section = await db.SchoolSections.SingleOrDefaultAsync(item => item.Id == sectionId, cancellationToken);
    if (section is null) return Results.NotFound(new { message = "Section not found." });
    if (request.IsActive is not bool isActive)
        return Results.BadRequest(new { message = "Choose whether to archive or restore the section." });
    section.IsActive = isActive;
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new { message = isActive ? "Section restored." : "Section archived." });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/admin/class-options/sections/{sectionId:int}/deletion-check", async (
    int sectionId,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var section = await db.SchoolSections.AsNoTracking()
        .SingleOrDefaultAsync(item => item.Id == sectionId, cancellationToken);
    if (section is null) return Results.NotFound(new { message = "Section not found." });
    var studentCount = await db.Users.CountAsync(user => user.SectionId == sectionId, cancellationToken);
    var reportCount = await db.Reports.CountAsync(report =>
        db.Users.Any(user => user.Id == report.UserId && user.SectionId == sectionId), cancellationToken);
    return Results.Ok(new { canDelete = studentCount == 0 && reportCount == 0, studentCount, reportCount });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapDelete("/api/admin/class-options/sections/{sectionId:int}", async (
    int sectionId,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    var section = await db.SchoolSections.SingleOrDefaultAsync(item => item.Id == sectionId, cancellationToken);
    if (section is null) return Results.NotFound(new { message = "Section not found." });
    var hasStudents = await db.Users.AnyAsync(user => user.SectionId == sectionId, cancellationToken);
    var hasReports = await db.Reports.AnyAsync(report =>
        db.Users.Any(user => user.Id == report.UserId && user.SectionId == sectionId), cancellationToken);
    if (hasStudents || hasReports)
        return Results.Conflict(new
        {
            message = "This section cannot be permanently deleted because students or reports are associated with it. Archive it instead.",
            hasStudents,
            hasReports
        });

    db.SchoolSections.Remove(section);
    try
    {
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch (DbUpdateException exception) when (exception.InnerException is SqliteException
        { SqliteExtendedErrorCode: 787 })
    {
        return Results.Conflict(new { message = "This section is still referenced by existing records. Archive it instead." });
    }
    return Results.Ok(new { message = "Section permanently deleted." });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/profile", async (HttpContext context, AppDbContext db) =>
{
    var userId = int.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var user = await db.Users.AsNoTracking().SingleAsync(item => item.Id == userId);
    var profile = new
    {
        user.Id,
        user.Username,
        user.Email,
        user.GradeLevelId,
        user.SectionId,
        GradeLevel = user.GradeLevelId == null
            ? null
            : await db.SchoolGrades.Where(grade => grade.Id == user.GradeLevelId)
                .Select(grade => grade.Name).SingleOrDefaultAsync(),
        Section = user.SectionId == null
            ? null
            : await db.SchoolSections.Where(section => section.Id == user.SectionId)
                .Select(section => section.Name).SingleOrDefaultAsync()
    };
    return Results.Ok(new { profile, grades = await GetClassDirectoryAsync(db) });
}).RequireAuthorization();

app.MapPatch("/api/profile/class", async (
    UpdateClassRequest request,
    HttpContext context,
    AppDbContext db) =>
{
    if (context.User.IsInRole("admin"))
        return Results.Forbid();
    if ((request.GradeLevelId is null) != (request.SectionId is null))
        return Results.BadRequest(new { message = "Choose a valid grade level and section from the school directory." });

    if (request.GradeLevelId is int gradeLevelId && request.SectionId is int sectionId &&
        (!await db.SchoolSections.AnyAsync(section =>
            section.Id == sectionId && section.GradeLevelId == gradeLevelId && section.IsActive) ||
         !await db.SchoolGrades.AnyAsync(grade => grade.Id == gradeLevelId && grade.IsActive)))
        return Results.BadRequest(new { message = "Choose an active grade level and section from the school directory." });

    var userId = int.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var user = await db.Users.SingleAsync(item => item.Id == userId);
    user.GradeLevelId = request.GradeLevelId;
    user.SectionId = request.SectionId;
    await db.SaveChangesAsync();
    return Results.Ok(new { message = request.GradeLevelId is null
        ? "Your class assignment was cleared."
        : "Your class details were saved." });
}).RequireAuthorization();

app.MapPost("/api/admin/class-options/import/preview", async (
    HttpRequest request,
    AppDbContext db) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { message = "Upload an .xlsx workbook." });
    if (request.ContentLength > 6 * 1024 * 1024)
        return Results.BadRequest(new { message = "The workbook must be 5 MB or smaller." });

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { message = "Choose an .xlsx workbook to preview." });
    if (!string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { message = "Only .xlsx workbooks are supported." });
    if (file.Length > 5 * 1024 * 1024)
        return Results.BadRequest(new { message = "The workbook must be 5 MB or smaller." });

    try
    {
        await using var stream = file.OpenReadStream();
        var rows = ReadClassOptionsWorkbook(stream);
        var preview = await BuildClassOptionsPreviewAsync(db, rows);
        return Results.Ok(preview);
    }
    catch (ClassOptionsImportException exception)
    {
        return Results.BadRequest(new { message = exception.Message, errors = exception.Errors });
    }
    catch (Exception exception) when (exception is InvalidDataException or IOException or
        ArgumentException or OpenXmlPackageException or XmlException)
    {
        return Results.BadRequest(new { message = "The workbook could not be read. Check that it is a valid .xlsx file." });
    }
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/admin/class-options/import", async (
    ClassOptionsImportRequest request,
    AppDbContext db) =>
{
    var validation = ValidateClassOptionRows(request.Rows);
    if (validation.Errors.Count > 0)
        return Results.BadRequest(new { message = "Fix the class list errors before importing.", errors = validation.Errors });
    if (validation.Rows.Count == 0)
        return Results.BadRequest(new { message = "The workbook must include at least one grade and section." });

    await using var transaction = await db.Database.BeginTransactionAsync();
    var currentPreview = await BuildClassOptionsPreviewAsync(db, validation.Rows);
    if (!string.Equals(request.Snapshot, currentPreview.Snapshot, StringComparison.Ordinal))
        return Results.Conflict(new { message = "Class options changed after this preview. Preview the workbook again before applying it." });

    var result = await ApplyClassOptionsAsync(db, validation.Rows);
    await transaction.CommitAsync();
    return Results.Ok(new { message = "Class options imported. Existing student assignments and reports were preserved.", result });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/admin/overview", async (
    int? page,
    int? pageSize,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var statusCounts = await db.Reports.AsNoTracking()
        .GroupBy(report => report.Status)
        .Select(group => new { Status = group.Key, Count = group.Count() })
        .ToDictionaryAsync(item => item.Status, item => item.Count);
    var gradeFolders = await db.SchoolGrades.AsNoTracking()
        .Where(grade => grade.IsActive)
        .OrderBy(grade => grade.SortOrder).ThenBy(grade => grade.Name)
        .Select(grade => new
        {
            grade.Id,
            grade.Name,
            ReportCount = db.Reports.Count(report => db.Users.Any(user =>
                user.Id == report.UserId && user.GradeLevelId == grade.Id)),
            UnreviewedCount = db.Reports.Count(report => !report.IsReviewed &&
                db.Users.Any(user => user.Id == report.UserId && user.GradeLevelId == grade.Id))
        }).ToListAsync(cancellationToken);
    var query = db.Reports.AsNoTracking()
        .Join(db.Users.AsNoTracking(), report => report.UserId, user => user.Id,
            (report, user) => new { Report = report, User = user });
    var totalReports = await query.CountAsync(cancellationToken);
    var currentPageSize = Math.Clamp(pageSize ?? 10, 1, 50);
    var currentPage = Math.Clamp(page ?? 1, 1, Math.Max(1, (int)Math.Ceiling(totalReports / (double)currentPageSize)));
    var reportRows = await query.OrderByDescending(item => item.Report.CreatedAt)
        .ThenByDescending(item => item.Report.Id)
        .Skip((currentPage - 1) * currentPageSize)
        .Take(currentPageSize)
        .Select(item => new
        {
            item.Report.Id,
            item.Report.Title,
            item.Report.Description,
            item.Report.Category,
            item.Report.Status,
            item.Report.Priority,
            item.Report.IsReviewed,
            item.Report.CreatedAt,
            Reporter = item.User.Username,
            GradeLevel = db.SchoolGrades.Where(grade => grade.Id == item.User.GradeLevelId)
                .Select(grade => grade.Name).FirstOrDefault(),
            Section = db.SchoolSections.Where(section => section.Id == item.User.SectionId)
                .Select(section => section.Name).FirstOrDefault()
        }).ToListAsync(cancellationToken);
    return Results.Ok(new
    {
        Total = statusCounts.Values.Sum(),
        Pending = statusCounts.GetValueOrDefault("Pending"),
        Acknowledged = statusCounts.GetValueOrDefault("Acknowledged"),
        InProgress = statusCounts.GetValueOrDefault("In Progress"),
        Resolved = statusCounts.GetValueOrDefault("Resolved"),
        Rejected = statusCounts.GetValueOrDefault("Rejected"),
        UnassignedReports = await db.Reports.CountAsync(report =>
            db.Users.Any(user => user.Id == report.UserId &&
                (user.GradeLevelId == null || user.SectionId == null)), cancellationToken),
        GradeFolders = gradeFolders,
        Reports = reportRows,
        Page = currentPage,
        PageSize = currentPageSize,
        TotalReports = totalReports,
        TotalPages = Math.Max(1, (int)Math.Ceiling(totalReports / (double)currentPageSize))
    });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/admin/report-folders/grades", async (
    bool? includeArchived,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var grades = await db.SchoolGrades.AsNoTracking()
        .Where(grade => includeArchived == true || grade.IsActive)
        .OrderBy(grade => grade.SortOrder).ThenBy(grade => grade.Name)
        .Select(grade => new
        {
            grade.Id,
            grade.Name,
            grade.IsActive,
            ReportCount = db.Reports.Count(report =>
                db.Users.Any(user => user.Id == report.UserId && user.GradeLevelId == grade.Id)),
            PendingReportCount = db.Reports.Count(report => !report.IsReviewed &&
                db.Users.Any(user => user.Id == report.UserId && user.GradeLevelId == grade.Id)),
            UnassignedReportCount = db.Reports.Count(report =>
                db.Users.Any(user => user.Id == report.UserId &&
                    user.GradeLevelId == grade.Id && user.SectionId == null)),
            SectionCount = db.SchoolSections.Count(section =>
                section.GradeLevelId == grade.Id &&
                (includeArchived == true || section.IsActive))
        }).ToListAsync(cancellationToken);
    return Results.Ok(grades);
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/admin/report-folders/grades/{gradeLevelId:int}/reports", async (
    int gradeLevelId,
    int? sectionId,
    string? search,
    string? status,
    string? category,
    string? sort,
    int? page,
    int? pageSize,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var grade = await db.SchoolGrades.AsNoTracking()
        .SingleOrDefaultAsync(item => item.Id == gradeLevelId, cancellationToken);
    if (grade is null)
        return Results.NotFound(new { message = "Grade level not found." });
    var supportedStatuses = new[] { "Pending", "Acknowledged", "In Progress", "Resolved", "Rejected" };
    var supportedCategories = new[] { "General", "Electrical", "Plumbing", "Cleaning", "Furniture", "Safety", "Other" };
    var supportedSorts = new[]
    {
        "newest", "oldest", "severity-desc", "severity-asc", "unresolved", "resolved",
        "recently-updated", "oldest-updated"
    };
    if (!string.IsNullOrWhiteSpace(status) && !supportedStatuses.Contains(status, StringComparer.Ordinal))
        return Results.BadRequest(new { message = "Choose a valid report status filter." });
    if (!string.IsNullOrWhiteSpace(category) && !supportedCategories.Contains(category, StringComparer.Ordinal))
        return Results.BadRequest(new { message = "Choose a valid report category filter." });
    if (!string.IsNullOrWhiteSpace(sort) && !supportedSorts.Contains(sort, StringComparer.Ordinal))
        return Results.BadRequest(new { message = "Choose a valid report sort order." });
    var sections = await db.SchoolSections.AsNoTracking()
        .Where(section => section.GradeLevelId == gradeLevelId && section.IsActive)
        .OrderBy(section => section.SortOrder).ThenBy(section => section.Name)
        .Select(section => new { section.Id, section.Name })
        .ToListAsync(cancellationToken);
    if (sectionId is not null && !await db.SchoolSections.AnyAsync(section =>
            section.Id == sectionId && section.GradeLevelId == gradeLevelId && section.IsActive, cancellationToken))
        return Results.BadRequest(new { message = "Choose an active section belonging to this grade level." });

    var query = db.Reports.AsNoTracking()
        .Join(db.Users.AsNoTracking(), report => report.UserId, user => user.Id,
            (report, user) => new { Report = report, User = user })
        .Where(item => item.User.GradeLevelId == gradeLevelId);
    if (sectionId is not null)
        query = query.Where(item => item.User.SectionId == sectionId);
    if (!string.IsNullOrWhiteSpace(status))
        query = query.Where(item => item.Report.Status == status);
    if (!string.IsNullOrWhiteSpace(category))
        query = query.Where(item => item.Report.Category == category);
    if (!string.IsNullOrWhiteSpace(search))
    {
        var term = search.Trim();
        query = query.Where(item =>
            item.Report.Title.Contains(term) ||
            item.Report.Location.Contains(term) ||
            item.Report.Category.Contains(term) ||
            item.Report.Description.Contains(term) ||
            item.User.Username.Contains(term));
    }

    query = sort switch
    {
        "oldest" => query.OrderBy(item => item.Report.CreatedAt),
        "severity-desc" => query.OrderBy(item => item.Report.Priority == "Critical" ? 0 :
                item.Report.Priority == "High" ? 1 : item.Report.Priority == "Normal" ? 2 : 3)
            .ThenByDescending(item => item.Report.CreatedAt),
        "severity-asc" => query.OrderBy(item => item.Report.Priority == "Low" ? 0 :
                item.Report.Priority == "Normal" ? 1 : item.Report.Priority == "High" ? 2 : 3)
            .ThenByDescending(item => item.Report.CreatedAt),
        "unresolved" => query.OrderBy(item => item.Report.Status == "Resolved" ? 1 : 0)
            .ThenByDescending(item => item.Report.CreatedAt),
        "resolved" => query.OrderBy(item => item.Report.Status == "Resolved" ? 0 : 1)
            .ThenByDescending(item => item.Report.CreatedAt),
        "recently-updated" => query.OrderByDescending(item => item.Report.UpdatedAt ?? item.Report.CreatedAt),
        "oldest-updated" => query.OrderBy(item => item.Report.UpdatedAt ?? item.Report.CreatedAt),
        _ => query.OrderByDescending(item => item.Report.CreatedAt).ThenByDescending(item => item.Report.Id)
    };
    var totalReports = await query.CountAsync(cancellationToken);
    var currentPageSize = Math.Clamp(pageSize ?? 20, 1, 100);
    var totalPages = Math.Max(1, (int)Math.Ceiling(totalReports / (double)currentPageSize));
    var currentPage = Math.Clamp(page ?? 1, 1, totalPages);
    var reportRows = await query.Skip((currentPage - 1) * currentPageSize)
        .Take(currentPageSize)
        .Select(item => new
        {
            item.Report.Id,
            item.Report.Title,
            item.Report.Description,
            item.Report.Category,
            item.Report.Location,
            item.Report.Status,
            item.Report.Priority,
            item.Report.IsReviewed,
            item.Report.Assignee,
            item.Report.CreatedAt,
            item.Report.UpdatedAt,
            Reporter = item.User.Username,
            Section = db.SchoolSections.Where(section => section.Id == item.User.SectionId)
                .Select(section => section.Name).FirstOrDefault(),
            HasPhoto = item.Report.Photo != null
        }).ToListAsync(cancellationToken);

    return Results.Ok(new
    {
        gradeLevel = grade.Name,
        sections,
        reports = reportRows,
        page = currentPage,
        pageSize = currentPageSize,
        totalReports,
        totalPages
    });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/admin/report-folders/grades/{gradeLevelId:int}/sections", async (
    int gradeLevelId,
    bool? includeArchived,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var gradeLevel = await db.SchoolGrades.AsNoTracking()
        .SingleOrDefaultAsync(grade => grade.Id == gradeLevelId, cancellationToken);
    if (gradeLevel is null)
        return Results.NotFound(new { message = "Grade level not found." });

    var sections = await db.SchoolSections.AsNoTracking()
        .Where(section => section.GradeLevelId == gradeLevelId &&
            (includeArchived == true || section.IsActive))
        .OrderBy(section => section.SortOrder).ThenBy(section => section.Name)
        .Select(section => new
        {
            section.Id,
            section.Name,
            section.IsActive,
            StudentCount = db.Users.Count(user =>
                user.SectionId == section.Id && user.GradeLevelId == gradeLevelId),
            ReportCount = db.Reports.Count(report => db.Users.Any(user =>
                user.Id == report.UserId &&
                user.SectionId == section.Id &&
                user.GradeLevelId == gradeLevelId)),
            PendingReportCount = db.Reports.Count(report => report.Status == "Pending" &&
                db.Users.Any(user => user.Id == report.UserId &&
                    user.SectionId == section.Id && user.GradeLevelId == gradeLevelId))
        }).ToListAsync(cancellationToken);
    return Results.Ok(new { gradeLevel = gradeLevel.Name, sections });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/admin/report-folders/sections/{sectionId:int}/reports", async (
    int sectionId,
    string? search,
    string? status,
    string? category,
    string? sort,
    AppDbContext db) =>
{
    var section = await db.SchoolSections.AsNoTracking()
        .Join(db.SchoolGrades.AsNoTracking(), item => item.GradeLevelId, grade => grade.Id,
            (item, grade) => new { Section = item, Grade = grade })
        .SingleOrDefaultAsync(item => item.Section.Id == sectionId);
    if (section is null)
        return Results.NotFound(new { message = "Section not found." });

    var query = db.Reports.AsNoTracking()
        .Join(db.Users.AsNoTracking(), report => report.UserId, user => user.Id,
            (report, user) => new { Report = report, User = user })
        .Where(item => item.User.SectionId == sectionId &&
                       item.User.GradeLevelId == section.Section.GradeLevelId);
    if (!string.IsNullOrWhiteSpace(status))
        query = query.Where(item => item.Report.Status == status);
    if (!string.IsNullOrWhiteSpace(category))
        query = query.Where(item => item.Report.Category == category);
    if (!string.IsNullOrWhiteSpace(search))
    {
        var term = search.Trim();
        query = query.Where(item =>
            item.Report.Title.Contains(term) ||
            item.Report.Location.Contains(term) ||
            item.Report.Category.Contains(term) ||
            item.Report.Description.Contains(term) ||
            item.User.Username.Contains(term));
    }

    query = sort switch
    {
        "oldest" => query.OrderBy(item => item.Report.CreatedAt),
        "status" => query.OrderBy(item => item.Report.Status).ThenByDescending(item => item.Report.CreatedAt),
        "priority" => query.OrderBy(item => item.Report.Priority == "High" ? 0 : item.Report.Priority == "Normal" ? 1 : 2)
            .ThenByDescending(item => item.Report.CreatedAt),
        _ => query.OrderByDescending(item => item.Report.CreatedAt)
    };
    var reports = await query.Select(item => new
    {
        item.Report.Id,
        item.Report.Title,
        item.Report.Description,
        item.Report.Category,
        item.Report.Location,
        item.Report.Status,
        item.Report.Priority,
        item.Report.Assignee,
        item.Report.CreatedAt,
        item.Report.UpdatedAt,
        Reporter = item.User.Username,
        HasPhoto = item.Report.Photo != null
    }).ToListAsync();
    return Results.Ok(new
    {
        gradeLevel = section.Grade.Name,
        section = section.Section.Name,
        reports
    });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/admin/report-folders/unassigned", async (
    string? search,
    string? status,
    string? category,
    string? sort,
    AppDbContext db) =>
{
    var query = db.Reports.AsNoTracking()
        .Join(db.Users.AsNoTracking(), report => report.UserId, user => user.Id,
            (report, user) => new { Report = report, User = user })
        .Where(item => item.User.GradeLevelId == null || item.User.SectionId == null);
    if (!string.IsNullOrWhiteSpace(status))
        query = query.Where(item => item.Report.Status == status);
    if (!string.IsNullOrWhiteSpace(category))
        query = query.Where(item => item.Report.Category == category);
    if (!string.IsNullOrWhiteSpace(search))
    {
        var term = search.Trim();
        query = query.Where(item =>
            item.Report.Title.Contains(term) ||
            item.Report.Location.Contains(term) ||
            item.Report.Category.Contains(term) ||
            item.Report.Description.Contains(term) ||
            item.User.Username.Contains(term));
    }
    query = sort switch
    {
        "oldest" => query.OrderBy(item => item.Report.CreatedAt),
        "status" => query.OrderBy(item => item.Report.Status).ThenByDescending(item => item.Report.CreatedAt),
        "priority" => query.OrderBy(item => item.Report.Priority == "High" ? 0 : item.Report.Priority == "Normal" ? 1 : 2)
            .ThenByDescending(item => item.Report.CreatedAt),
        _ => query.OrderByDescending(item => item.Report.CreatedAt)
    };
    var reports = await query.Select(item => new
    {
        item.Report.Id,
        item.Report.Title,
        item.Report.Description,
        item.Report.Category,
        item.Report.Location,
        item.Report.Status,
        item.Report.Priority,
        item.Report.CreatedAt,
        Reporter = item.User.Username,
        GradeLevel = db.SchoolGrades.Where(grade => grade.Id == item.User.GradeLevelId)
            .Select(grade => grade.Name).FirstOrDefault(),
        Section = db.SchoolSections.Where(section => section.Id == item.User.SectionId)
            .Select(section => section.Name).FirstOrDefault()
    }).ToListAsync();
    return Results.Ok(reports);
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/reports", async (HttpContext context, AppDbContext db) =>
{
    var isAdmin = context.User.IsInRole("admin");
    var userId = int.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var query = db.Reports.AsNoTracking();
    if (!isAdmin)
        query = query.Where(report => report.UserId == userId);

    var reports = await query.OrderByDescending(report => report.CreatedAt)
        .Select(report => new
        {
            report.Id,
            report.UserId,
            report.Title,
            report.Description,
            report.Category,
            report.Location,
            report.Status,
            report.Priority,
            report.Assignee,
            report.PublicUpdate,
            AdminNotes = isAdmin ? report.AdminNotes : null,
            report.CreatedAt,
            report.UpdatedAt,
            Reporter = db.Users.Where(user => user.Id == report.UserId)
                .Select(user => user.Username).FirstOrDefault(),
            GradeLevel = db.Users.Where(user => user.Id == report.UserId)
                .Join(db.SchoolGrades, user => user.GradeLevelId, grade => grade.Id,
                    (user, grade) => grade.Name).FirstOrDefault(),
            Section = db.Users.Where(user => user.Id == report.UserId)
                .Join(db.SchoolSections, user => user.SectionId, section => section.Id,
                    (user, section) => section.Name).FirstOrDefault(),
            HasPhoto = report.Photo != null
        }).ToListAsync();
    return Results.Ok(reports);
}).RequireAuthorization();

app.MapGet("/api/reports/{id:int}", async (int id, HttpContext context, AppDbContext db) =>
{
    var userId = int.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var report = await db.Reports.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id);
    if (report is null)
        return Results.NotFound(new { message = "Report not found." });
    if (!context.User.IsInRole("admin") && report.UserId != userId)
        return Results.NotFound(new { message = "Report not found." });

    var reporter = await db.Users.Where(user => user.Id == report.UserId)
        .Select(user => user.Username).SingleOrDefaultAsync();
    var reporterClass = await db.Users.AsNoTracking()
        .Where(user => user.Id == report.UserId)
        .Select(user => new
        {
            GradeLevel = db.SchoolGrades.Where(grade => grade.Id == user.GradeLevelId)
                .Select(grade => grade.Name).FirstOrDefault(),
            Section = db.SchoolSections.Where(section => section.Id == user.SectionId)
                .Select(section => section.Name).FirstOrDefault()
        }).SingleOrDefaultAsync();
    var isAdmin = context.User.IsInRole("admin");
    var history = await db.ReportActivities.AsNoTracking()
        .Where(activity => activity.ReportId == report.Id)
        .Where(activity => context.User.IsInRole("admin") || activity.Action != "Admin note added")
        .OrderBy(activity => activity.CreatedAt)
        .Select(activity => new
        {
            activity.Action,
            activity.Details,
            activity.CreatedAt,
            Actor = db.Users.Where(user => user.Id == activity.ActorUserId)
                .Select(user => user.Username).FirstOrDefault()
        }).ToListAsync();
    return Results.Ok(new
    {
        report.Id,
        report.Title,
        report.Description,
        report.Category,
        report.Location,
        report.Status,
        report.Priority,
        report.Assignee,
        report.PublicUpdate,
        report.CreatedAt,
        report.UpdatedAt,
        Reporter = isAdmin ? reporter : null,
        GradeLevel = isAdmin ? reporterClass?.GradeLevel : null,
        Section = isAdmin ? reporterClass?.Section : null,
        AdminNotes = isAdmin ? report.AdminNotes : null,
        Photo = report.Photo is null ? null : $"data:{report.PhotoContentType ?? "image/jpeg"};base64,{Convert.ToBase64String(report.Photo)}",
        History = history
    });
}).RequireAuthorization();

app.MapPost("/api/reports", async (
    CreateReportRequest request,
    HttpContext context,
    AppDbContext db) =>
{
    var title = request.Title?.Trim();
    var description = request.Description?.Trim();
    var category = request.Category?.Trim();
    var location = request.Location?.Trim();
    var supportedCategories = new[] { "General", "Electrical", "Plumbing", "Cleaning", "Furniture", "Safety", "Other" };
    if (string.IsNullOrWhiteSpace(title) || title.Length > 100 ||
        string.IsNullOrWhiteSpace(description) || description.Length > 1000 ||
        string.IsNullOrWhiteSpace(category) || !supportedCategories.Contains(category, StringComparer.Ordinal) ||
        string.IsNullOrWhiteSpace(location) || location.Length > 160)
        return Results.BadRequest(new { message = "Enter a valid title, category, location, and description." });

    byte[]? photo = null;
    string? contentType = null;
    if (!string.IsNullOrWhiteSpace(request.Image))
    {
        var separator = request.Image.IndexOf(',');
        var metadata = separator >= 0 ? request.Image[..separator] : "";
        if (!metadata.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) ||
            !(metadata.Contains("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
              metadata.Contains("image/png", StringComparison.OrdinalIgnoreCase) ||
              metadata.Contains("image/gif", StringComparison.OrdinalIgnoreCase) ||
              metadata.Contains("image/webp", StringComparison.OrdinalIgnoreCase)) ||
            separator < 0 ||
            !Convert.TryFromBase64String(request.Image[(separator + 1)..], new byte[1_048_577], out _))
            return Results.BadRequest(new { message = "Choose a valid JPEG, PNG, GIF, or WebP image smaller than 1 MB." });

        try { photo = Convert.FromBase64String(request.Image[(separator + 1)..]); }
        catch (FormatException) { return Results.BadRequest(new { message = "The uploaded image is invalid." }); }
        if (photo.Length > 1_048_576)
            return Results.BadRequest(new { message = "Choose an image smaller than 1 MB." });
        contentType = metadata["data:".Length..].Split(';')[0];
    }

    var userId = int.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var report = new MaintenanceReport
    {
        UserId = userId,
        Title = title,
        Description = description,
        Category = category,
        Location = location,
        Status = "Pending",
        Priority = "Normal",
        Photo = photo,
        PhotoContentType = contentType,
        CreatedAt = DateTime.UtcNow
    };
    await using var transaction = await db.Database.BeginTransactionAsync();
    db.Reports.Add(report);
    await db.SaveChangesAsync();
    db.ReportActivities.Add(new ReportActivity
    {
        ReportId = report.Id,
        ActorUserId = userId,
        Action = "Submitted",
        Details = "Report submitted."
    });
    var administratorIds = await db.Users.Where(user => user.Role == "admin")
        .Select(user => user.Id).ToListAsync();
    db.ReportNotifications.AddRange(administratorIds.Select(adminId => new ReportNotification
    {
        RecipientUserId = adminId,
        ReportId = report.Id,
        Message = $"New maintenance report: {report.Title}."
    }));
    await db.SaveChangesAsync();
    await transaction.CommitAsync();
    return Results.Created($"/api/reports/{report.Id}", new { report.Id, message = "Report submitted." });
}).RequireAuthorization().RequireRateLimiting("auth");

app.MapPatch("/api/reports/{id:int}", async (
    int id,
    UpdateReportRequest request,
    HttpContext context,
    AppDbContext db) =>
{
    var report = await db.Reports.SingleOrDefaultAsync(item => item.Id == id);
    if (report is null)
        return Results.NotFound(new { message = "Report not found." });

    var actorId = int.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    if (!context.User.IsInRole("admin"))
        return Results.Forbid();

    var changes = new List<(string Action, string Details)>();
    var statuses = new[] { "Pending", "Acknowledged", "In Progress", "Resolved", "Rejected" };
    var priorities = new[] { "Low", "Normal", "High", "Critical" };
    if (request.Status is not null)
    {
        if (!statuses.Contains(request.Status, StringComparer.Ordinal))
            return Results.BadRequest(new { message = "Choose a valid report status." });
        if (request.Status == "Rejected" &&
            string.IsNullOrWhiteSpace(request.PublicUpdate ?? report.PublicUpdate))
            return Results.BadRequest(new { message = "Add a brief explanation for the student before rejecting this report." });
        if (report.Status != request.Status)
        {
            changes.Add(("Status changed", $"{report.Status} → {request.Status}"));
            report.Status = request.Status;
            report.ResolvedAt = request.Status == "Resolved" ? DateTime.UtcNow : null;
        }
    }
    if (request.Priority is not null)
    {
        if (!priorities.Contains(request.Priority, StringComparer.Ordinal))
            return Results.BadRequest(new { message = "Choose Low, Normal, High, or Critical priority." });
        if (report.Priority != request.Priority)
        {
            changes.Add(("Priority changed", $"{report.Priority} → {request.Priority}"));
            report.Priority = request.Priority;
        }
    }
    if (request.Assignee is not null)
    {
        var assignee = request.Assignee.Trim();
        if (assignee.Length > 60)
            return Results.BadRequest(new { message = "Assignment must be 60 characters or fewer." });
        if (report.Assignee != assignee)
        {
            changes.Add(("Assignment changed", string.IsNullOrEmpty(assignee) ? "Assignment cleared." : $"Assigned to {assignee}."));
            report.Assignee = string.IsNullOrEmpty(assignee) ? null : assignee;
        }
    }
    if (request.AdminNotes is not null)
    {
        var notes = request.AdminNotes.Trim();
        if (notes.Length > 1000)
            return Results.BadRequest(new { message = "Notes must be 1,000 characters or fewer." });
        if (report.AdminNotes != notes)
        {
            changes.Add(("Admin note added", notes));
            report.AdminNotes = notes;
        }
    }
    if (request.PublicUpdate is not null)
    {
        var update = request.PublicUpdate.Trim();
        if (update.Length > 500)
            return Results.BadRequest(new { message = "Student updates must be 500 characters or fewer." });
        if (report.PublicUpdate != update)
        {
            report.PublicUpdate = update;
            changes.Add(("Public update", update));
        }
    }
    report.UpdatedAt = DateTime.UtcNow;
    foreach (var change in changes)
        db.ReportActivities.Add(new ReportActivity
        {
            ReportId = report.Id,
            ActorUserId = actorId,
            Action = change.Action,
            Details = change.Details
        });
    if (changes.Any(change => change.Action is "Status changed" or "Public update"))
        db.ReportNotifications.Add(new ReportNotification
        {
            RecipientUserId = report.UserId,
            ReportId = report.Id,
            Message = string.IsNullOrWhiteSpace(report.PublicUpdate)
                ? $"Your report “{report.Title}” is now {report.Status}."
                : $"Your report “{report.Title}” is now {report.Status}: {report.PublicUpdate}"
        });
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Report updated." });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/admin/reports/{id:int}/review", async (
    int id,
    HttpContext context,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var report = await db.Reports.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    if (report is null)
        return Results.NotFound(new { message = "Report not found." });
    if (!report.IsReviewed)
    {
        report.IsReviewed = true;
        report.UpdatedAt = DateTime.UtcNow;
        db.ReportActivities.Add(new ReportActivity
        {
            ReportId = report.Id,
            ActorUserId = int.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            Action = "Marked reviewed",
            Details = "Report marked as reviewed."
        });
        await db.SaveChangesAsync(cancellationToken);
    }
    return Results.Ok(new { message = "Report marked as reviewed." });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapDelete("/api/reports/{id:int}", async (int id, AppDbContext db) =>
{
    var report = await db.Reports.SingleOrDefaultAsync(item => item.Id == id);
    if (report is null)
        return Results.NotFound(new { message = "Report not found." });
    db.ReportActivities.RemoveRange(db.ReportActivities.Where(item => item.ReportId == id));
    db.ReportNotifications.RemoveRange(db.ReportNotifications.Where(item => item.ReportId == id));
    db.Reports.Remove(report);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Report deleted." });
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapGet("/api/notifications", async (HttpContext context, AppDbContext db) =>
{
    var userId = int.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var query = db.ReportNotifications.AsNoTracking()
        .Where(notification => notification.RecipientUserId == userId);
    var notifications = await query.OrderByDescending(notification => notification.CreatedAt)
        .Take(50)
        .Select(notification => new
        {
            notification.Id,
            notification.ReportId,
            notification.Message,
            notification.CreatedAt,
            notification.ReadAt
        }).ToListAsync();
    return Results.Ok(notifications);
}).RequireAuthorization();

app.MapGet("/api/activity", async (AppDbContext db) =>
{
    var activity = await db.ReportActivities.AsNoTracking()
        .OrderByDescending(item => item.CreatedAt)
        .Take(100)
        .Select(item => new
        {
            item.ReportId,
            item.Action,
            item.Details,
            item.CreatedAt,
            Actor = db.Users.Where(user => user.Id == item.ActorUserId)
                .Select(user => user.Username).FirstOrDefault(),
            ReportTitle = db.Reports.Where(report => report.Id == item.ReportId)
                .Select(report => report.Title).FirstOrDefault()
        }).ToListAsync();
    return Results.Ok(activity);
}).RequireAuthorization(policy => policy.RequireRole("admin"));

app.MapPost("/api/notifications/{id:int}/read", async (int id, HttpContext context, AppDbContext db) =>
{
    var userId = int.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var notification = await db.ReportNotifications.SingleOrDefaultAsync(item =>
        item.Id == id && item.RecipientUserId == userId);
    if (notification is null)
        return Results.NotFound(new { message = "Notification not found." });
    notification.ReadAt ??= DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Notification marked as read." });
}).RequireAuthorization();

app.MapPost("/api/profile", async (
    UpdateProfileRequest request,
    HttpContext context,
    AppDbContext db) =>
{
    var name = NormalizePersonName(request.Username);
    if (name is null || !IsValidPersonName(name))
        return Results.BadRequest(new { message = "Name may contain letters, spaces, hyphens, and apostrophes only." });
    if (name.Length < 3 || name.Length > 50)
        return Results.BadRequest(new { message = "Name must contain 3–50 characters." });
    var userId = int.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    if (await db.Users.AnyAsync(user => user.Id != userId && user.Username == name))
        return Results.Conflict(new { message = "That name is already in use." });
    var user = await db.Users.SingleAsync(user => user.Id == userId);
    user.Username = name;
    await db.SaveChangesAsync();
    var identity = context.User.Identity as ClaimsIdentity;
    var nameClaim = identity?.FindFirst(ClaimTypes.Name);
    if (identity is not null && nameClaim is not null)
    {
        identity.RemoveClaim(nameClaim);
        identity.AddClaim(new Claim(ClaimTypes.Name, name));
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, context.User);
    }
    return Results.Ok(new { username = user.Username, message = "Profile updated." });
}).RequireAuthorization();

app.Run();

static string? NormalizeEmail(string? email) =>
    string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

static string? NormalizePersonName(string? name)
{
    if (string.IsNullOrWhiteSpace(name)) return null;
    return string.Join(" ", name.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

static bool IsValidPersonName(string name)
{
    if (string.IsNullOrWhiteSpace(name)) return false;
    foreach (var character in name)
    {
        if (!char.IsLetter(character) && !char.IsWhiteSpace(character) &&
            character != '-' && character != '\'' && character != '\u2019')
            return false;
    }
    return true;
}

static bool IsValidEmail(string email)
{
    try
    {
        var address = new MailAddress(email);
        return string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase);
    }
    catch { return false; }
}

static byte[] HashVerificationCode(string code, byte[] salt) =>
    Rfc2898DeriveBytes.Pbkdf2(code, salt, 210_000, HashAlgorithmName.SHA256, 32);

static string SetRegistrationCode(PendingRegistration pending, DateTime now)
{
    var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    var salt = RandomNumberGenerator.GetBytes(16);
    pending.CodeHash = HashVerificationCode(code, salt);
    pending.CodeSalt = salt;
    pending.CreatedUtc = now;
    pending.ExpiresUtc = now.AddMinutes(10);
    pending.Attempts = 0;
    return code;
}

static void InvalidateRegistrationCode(PendingRegistration pending)
{
    pending.CodeHash = null;
    pending.CodeSalt = null;
    pending.ExpiresUtc = DateTime.UtcNow;
}

static async Task<List<GradeDirectoryResponse>> GetClassDirectoryAsync(AppDbContext db)
{
    var grades = await db.SchoolGrades.AsNoTracking()
        .Where(grade => grade.IsActive)
        .OrderBy(grade => grade.SortOrder).ThenBy(grade => grade.Name)
        .Select(grade => new GradeDirectoryResponse(grade.Id, grade.Name, new List<SectionDirectoryResponse>()))
        .ToListAsync();
    var sections = await db.SchoolSections.AsNoTracking()
        .Where(section => section.IsActive && db.SchoolGrades.Any(grade =>
            grade.Id == section.GradeLevelId && grade.IsActive))
        .OrderBy(section => section.SortOrder).ThenBy(section => section.Name)
        .Select(section => new SectionDirectoryResponse(section.Id, section.Name, section.GradeLevelId))
        .ToListAsync();
    var gradeById = grades.ToDictionary(grade => grade.Id);
    foreach (var section in sections)
        if (gradeById.TryGetValue(section.GradeLevelId, out var grade))
            grade.Sections.Add(section);
    return grades;
}

static List<ClassOptionImportRow> ReadClassOptionsWorkbook(Stream stream)
{
    using var workbook = new XLWorkbook(stream);
    var worksheet = workbook.Worksheets.FirstOrDefault()
        ?? throw new ClassOptionsImportException("The workbook does not contain a worksheet.");
    var usedRange = worksheet.RangeUsed()
        ?? throw new ClassOptionsImportException("The worksheet is empty. Add Grade Level and Section columns.");
    var headerRow = 1;
    if (usedRange.FirstRow().RowNumber() != headerRow)
        throw new ClassOptionsImportException("Grade Level and Section headings must be in the first row.");
    var lastColumn = usedRange.LastColumn().ColumnNumber();
    var gradeColumn = 0;
    var sectionColumn = 0;
    for (var column = usedRange.FirstColumn().ColumnNumber(); column <= lastColumn; column++)
    {
        var header = worksheet.Cell(headerRow, column).GetString().Trim();
        if (string.Equals(header, "Grade Level", StringComparison.OrdinalIgnoreCase))
        {
            if (gradeColumn != 0)
                throw new ClassOptionsImportException("The Grade Level column appears more than once.");
            gradeColumn = column;
        }
        if (string.Equals(header, "Section", StringComparison.OrdinalIgnoreCase))
        {
            if (sectionColumn != 0)
                throw new ClassOptionsImportException("The Section column appears more than once.");
            sectionColumn = column;
        }
    }
    if (gradeColumn == 0 || sectionColumn == 0)
        throw new ClassOptionsImportException("The first worksheet must contain Grade Level and Section columns.");

    var lastRow = usedRange.LastRow().RowNumber();
    if (lastRow - headerRow > 2000)
        throw new ClassOptionsImportException("The class list cannot contain more than 2,000 data rows.");
    var rows = new List<ClassOptionImportRow>();
    var errors = new List<string>();
    for (var rowNumber = headerRow + 1; rowNumber <= lastRow; rowNumber++)
    {
        var gradeCell = worksheet.Cell(rowNumber, gradeColumn);
        var sectionCell = worksheet.Cell(rowNumber, sectionColumn);
        if (gradeCell.HasFormula || sectionCell.HasFormula)
        {
            errors.Add($"Row {rowNumber}: formulas are not allowed; enter class names as values.");
            continue;
        }
        var grade = gradeCell.GetFormattedString().Trim();
        var section = sectionCell.GetFormattedString().Trim();
        if (grade.Length == 0 && section.Length == 0) continue;
        if (grade.Length == 0 || section.Length == 0)
        {
            errors.Add($"Row {rowNumber}: both Grade Level and Section are required.");
            continue;
        }
        rows.Add(new ClassOptionImportRow(grade, section));
    }

    var validation = ValidateClassOptionRows(rows);
    errors.AddRange(validation.Errors);
    if (errors.Count > 0)
        throw new ClassOptionsImportException("The class list contains invalid entries.", errors);
    return validation.Rows;
}

static ClassOptionsValidation ValidateClassOptionRows(IReadOnlyCollection<ClassOptionImportRow>? rows)
{
    var errors = new List<string>();
    var normalized = new List<ClassOptionImportRow>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (rows is null)
        return new ClassOptionsValidation(normalized, ["The import preview is missing class rows."]);
    if (rows.Count > 2000)
        return new ClassOptionsValidation(normalized, ["The class list cannot contain more than 2,000 rows."]);

    var rowNumber = 1;
    foreach (var row in rows)
    {
        rowNumber++;
        if (row is null)
        {
            errors.Add($"Entry {rowNumber}: the row is empty.");
            continue;
        }
        var grade = NormalizeClassOptionName(row.GradeLevel ?? "");
        var section = NormalizeClassOptionName(row.Section ?? "");
        if (grade.Length == 0 || section.Length == 0)
        {
            errors.Add($"Entry {rowNumber}: both Grade Level and Section are required.");
            continue;
        }
        if (grade.Length > 40 || section.Length > 80)
        {
            errors.Add($"Entry {rowNumber}: grade levels are limited to 40 characters and sections to 80.");
            continue;
        }
        if (grade.Any(char.IsControl) || section.Any(char.IsControl))
        {
            errors.Add($"Entry {rowNumber}: class names cannot contain control characters.");
            continue;
        }
        var key = ClassOptionKey(grade, section);
        if (!seen.Add(key))
        {
            errors.Add($"Entry {rowNumber}: “{grade} · {section}” is listed more than once.");
            continue;
        }
        normalized.Add(new ClassOptionImportRow(grade, section));
    }
    return new ClassOptionsValidation(normalized, errors);
}

static string? NormalizeClassFolderName(string? value, int maxLength)
{
    var name = value is null ? null : NormalizeClassOptionName(value);
    return string.IsNullOrWhiteSpace(name) ||
           name.Length > maxLength ||
           name.Any(char.IsControl)
        ? null
        : name;
}

static string NormalizeClassOptionName(string value) =>
    string.Join(" ", value.Normalize(NormalizationForm.FormKC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

static string ClassNameKey(string name) =>
    NormalizeClassOptionName(name).ToUpperInvariant();

static async Task<ClassOptionsPreview> BuildClassOptionsPreviewAsync(
    AppDbContext db,
    IReadOnlyList<ClassOptionImportRow> rows)
{
    var currentGrades = await db.SchoolGrades.AsNoTracking().ToListAsync();
    var currentSections = await db.SchoolSections.AsNoTracking().ToListAsync();
    var dependencies = await GetClassOptionDependenciesAsync(db, currentGrades, currentSections);
    var snapshot = BuildClassOptionsSnapshot(currentGrades, currentSections);
    var importedGradeNames = rows.Select(row => row.GradeLevel)
        .GroupBy(ClassNameKey, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First()).ToList();
    var gradeByKey = new Dictionary<string, SchoolGrade?>(StringComparer.OrdinalIgnoreCase);
    foreach (var name in importedGradeNames)
        gradeByKey[ClassNameKey(name)] = FindGradeMatch(currentGrades, name);

    var selectedGradeIds = gradeByKey.Values.Where(grade => grade is not null)
        .Select(grade => grade!.Id).ToHashSet();
    var gradesToAdd = importedGradeNames.Where(name => gradeByKey[ClassNameKey(name)] is null).ToList();
    var gradesToRestore = new List<string>();
    var gradesToRetain = new List<string>();
    var gradesToUpdate = new List<ClassOptionUpdate>();
    foreach (var name in importedGradeNames)
    {
        var grade = gradeByKey[ClassNameKey(name)];
        if (grade is null) continue;
        if (!grade.IsActive) gradesToRestore.Add(name);
        if (!string.Equals(grade.Name, name, StringComparison.Ordinal))
            gradesToUpdate.Add(new ClassOptionUpdate(null, grade.Name, name));
        else if (grade.IsActive)
            gradesToRetain.Add(name);
    }

    var selectedSectionIds = new HashSet<int>();
    var sectionsToAdd = new List<ClassOptionEntry>();
    var sectionsToRestore = new List<ClassOptionEntry>();
    var sectionsToRetain = new List<ClassOptionEntry>();
    var sectionsToUpdate = new List<ClassOptionUpdate>();
    foreach (var row in rows)
    {
        var grade = gradeByKey[ClassNameKey(row.GradeLevel)];
        var match = grade is null ? null : FindSectionMatch(currentSections, grade.Id, row.Section);
        if (match is null)
        {
            sectionsToAdd.Add(new ClassOptionEntry(row.GradeLevel, row.Section));
            continue;
        }

        selectedSectionIds.Add(match.Id);
        if (!match.IsActive)
            sectionsToRestore.Add(new ClassOptionEntry(row.GradeLevel, row.Section));
        if (!string.Equals(match.Name, row.Section, StringComparison.Ordinal))
            sectionsToUpdate.Add(new ClassOptionUpdate(match.Name, row.Section, row.GradeLevel));
        else if (match.IsActive)
            sectionsToRetain.Add(new ClassOptionEntry(row.GradeLevel, row.Section));
    }

    var gradesToDelete = new List<ClassOptionRemoval>();
    var gradesToArchive = new List<ClassOptionRemoval>();
    foreach (var grade in currentGrades.Where(item => item.IsActive && !selectedGradeIds.Contains(item.Id)))
    {
        var dependency = dependencies.Grades[grade.Id];
        var removal = new ClassOptionRemoval(grade.Name, grade.Name, dependency.StudentCount, dependency.ReportCount);
        (dependency.HasDependencies ? gradesToArchive : gradesToDelete).Add(removal);
    }

    var sectionParentById = currentGrades.ToDictionary(grade => grade.Id);
    var sectionsToDelete = new List<ClassOptionRemoval>();
    var sectionsToArchive = new List<ClassOptionRemoval>();
    foreach (var section in currentSections.Where(item => item.IsActive && !selectedSectionIds.Contains(item.Id)))
    {
        var dependency = dependencies.Sections[section.Id];
        var removal = new ClassOptionRemoval(
            sectionParentById[section.GradeLevelId].Name, section.Name,
            dependency.StudentCount, dependency.ReportCount);
        (dependency.HasDependencies ? sectionsToArchive : sectionsToDelete).Add(removal);
    }

    return new ClassOptionsPreview(rows, gradesToAdd, gradesToRestore, gradesToUpdate, gradesToRetain,
        gradesToDelete, gradesToArchive, sectionsToAdd, sectionsToRestore, sectionsToUpdate, sectionsToRetain,
        sectionsToDelete, sectionsToArchive, snapshot);
}

static async Task<ClassOptionsImportResult> ApplyClassOptionsAsync(
    AppDbContext db,
    IReadOnlyList<ClassOptionImportRow> rows)
{
    var grades = await db.SchoolGrades.ToListAsync();
    var sections = await db.SchoolSections.ToListAsync();
    var dependencies = await GetClassOptionDependenciesAsync(db, grades, sections);
    var gradeNames = rows.Select(row => row.GradeLevel)
        .GroupBy(ClassNameKey, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First()).ToList();
    var addedGrades = 0;
    var addedSections = 0;
    var restoredGrades = 0;
    var restoredSections = 0;
    var deletedGrades = 0;
    var archivedGrades = 0;
    var deletedSections = 0;
    var archivedSections = 0;
    var targetGrades = new Dictionary<string, SchoolGrade>(StringComparer.OrdinalIgnoreCase);
    var selectedGradeIds = new HashSet<int>();

    for (var index = 0; index < gradeNames.Count; index++)
    {
        var name = gradeNames[index];
        var key = ClassNameKey(name);
        var grade = FindGradeMatch(grades, name);
        if (grade is null)
        {
            grade = new SchoolGrade { Name = name };
            grades.Add(grade);
            db.SchoolGrades.Add(grade);
            addedGrades++;
        }
        else if (!grade.IsActive)
        {
            restoredGrades++;
        }
        grade.Name = name;
        grade.IsActive = true;
        grade.SortOrder = index + 1;
        targetGrades.Add(key, grade);
        selectedGradeIds.Add(grade.Id);
    }

    await db.SaveChangesAsync();
    selectedGradeIds.Clear();
    selectedGradeIds.UnionWith(targetGrades.Values.Select(grade => grade.Id));

    var sectionOrderByGrade = new Dictionary<int, int>();
    var selectedSections = new List<SchoolSection>();
    foreach (var row in rows)
    {
        var grade = targetGrades[ClassNameKey(row.GradeLevel)];
        sectionOrderByGrade.TryGetValue(grade.Id, out var currentOrder);
        var section = FindSectionMatch(sections, grade.Id, row.Section);
        if (section is null)
        {
            section = new SchoolSection { GradeLevelId = grade.Id, Name = row.Section };
            sections.Add(section);
            db.SchoolSections.Add(section);
            addedSections++;
        }
        else if (!section.IsActive)
        {
            restoredSections++;
        }
        section.Name = row.Section;
        section.IsActive = true;
        section.SortOrder = ++currentOrder;
        sectionOrderByGrade[grade.Id] = currentOrder;
        selectedSections.Add(section);
    }

    await db.SaveChangesAsync();
    var selectedSectionIds = selectedSections.Select(section => section.Id).ToHashSet();

    foreach (var section in sections.Where(item => item.IsActive && !selectedSectionIds.Contains(item.Id)).ToList())
    {
        if (dependencies.Sections[section.Id].HasDependencies)
        {
            section.IsActive = false;
            archivedSections++;
        }
        else
        {
            db.SchoolSections.Remove(section);
            sections.Remove(section);
            deletedSections++;
        }
    }

    await db.SaveChangesAsync();
    foreach (var grade in grades.Where(item => item.IsActive && !selectedGradeIds.Contains(item.Id)).ToList())
    {
        if (dependencies.Grades[grade.Id].HasDependencies)
        {
            grade.IsActive = false;
            archivedGrades++;
        }
        else
        {
            foreach (var section in sections.Where(item => item.GradeLevelId == grade.Id).ToList())
            {
                db.SchoolSections.Remove(section);
                sections.Remove(section);
                deletedSections++;
            }
            db.SchoolGrades.Remove(grade);
            grades.Remove(grade);
            deletedGrades++;
        }
    }

    await db.SaveChangesAsync();
    return new ClassOptionsImportResult(addedGrades, restoredGrades, deletedGrades, archivedGrades,
        addedSections, restoredSections, deletedSections, archivedSections);
}

static string ClassOptionKey(string grade, string section) =>
    $"{ClassNameKey(grade)}\u001f{ClassNameKey(section)}";

static SchoolGrade? FindGradeMatch(IEnumerable<SchoolGrade> grades, string name) =>
    grades.Where(grade => ClassNameKey(grade.Name) == ClassNameKey(name))
        .OrderBy(grade => string.Equals(grade.Name, name, StringComparison.Ordinal) ? 0 : 1)
        .ThenBy(grade => grade.IsActive ? 0 : 1)
        .ThenBy(grade => grade.Id)
        .FirstOrDefault();

static SchoolSection? FindSectionMatch(IEnumerable<SchoolSection> sections, int gradeLevelId, string name) =>
    sections.Where(section => section.GradeLevelId == gradeLevelId &&
            ClassNameKey(section.Name) == ClassNameKey(name))
        .OrderBy(section => string.Equals(section.Name, name, StringComparison.Ordinal) ? 0 : 1)
        .ThenBy(section => section.IsActive ? 0 : 1)
        .ThenBy(section => section.Id)
        .FirstOrDefault();

static async Task<(Dictionary<int, ClassOptionDependency> Grades, Dictionary<int, ClassOptionDependency> Sections)>
    GetClassOptionDependenciesAsync(
        AppDbContext db,
        IReadOnlyCollection<SchoolGrade> grades,
        IReadOnlyCollection<SchoolSection> sections)
{
    var assignments = await db.Users.AsNoTracking()
        .Select(user => new { user.Id, user.GradeLevelId, user.SectionId })
        .ToListAsync();
    var reportCounts = await db.Reports.AsNoTracking()
        .GroupBy(report => report.UserId)
        .Select(group => new { UserId = group.Key, Count = group.Count() })
        .ToDictionaryAsync(item => item.UserId, item => item.Count);
    var sectionsByGrade = sections.GroupBy(section => section.GradeLevelId)
        .ToDictionary(group => group.Key, group => group.Select(section => section.Id).ToHashSet());
    var sectionDependencies = sections.ToDictionary(section => section.Id, section =>
    {
        var assignedUsers = assignments.Where(user => user.SectionId == section.Id).ToList();
        return new ClassOptionDependency(
            assignedUsers.Count,
            assignedUsers.Sum(user => reportCounts.GetValueOrDefault(user.Id)));
    });
    var gradeDependencies = grades.ToDictionary(grade => grade.Id, grade =>
    {
        var sectionIds = sectionsByGrade.GetValueOrDefault(grade.Id) ?? [];
        var assignedUsers = assignments.Where(user =>
            user.GradeLevelId == grade.Id ||
            (user.SectionId is int sectionId && sectionIds.Contains(sectionId))).ToList();
        return new ClassOptionDependency(
            assignedUsers.Count,
            assignedUsers.Sum(user => reportCounts.GetValueOrDefault(user.Id)));
    });
    return (gradeDependencies, sectionDependencies);
}

static string BuildClassOptionsSnapshot(
    IEnumerable<SchoolGrade> grades,
    IEnumerable<SchoolSection> sections)
{
    var gradeNames = grades.ToDictionary(grade => grade.Id, grade => grade.Name);
    var current = grades.Select(grade => $"G|{grade.Id}|{ClassNameKey(grade.Name)}|{grade.Name}|{grade.IsActive}")
        .Concat(sections.Select(section =>
            $"S|{section.Id}|{section.GradeLevelId}|{ClassNameKey(gradeNames[section.GradeLevelId])}|{ClassNameKey(section.Name)}|{section.Name}|{section.IsActive}"))
        .OrderBy(item => item, StringComparer.OrdinalIgnoreCase);
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', current))));
}

static async Task EnsureSchoolSchemaAsync(AppDbContext db)
{
    var connection = db.Database.GetDbConnection();
    var openedHere = connection.State != System.Data.ConnectionState.Open;
    if (openedHere) await connection.OpenAsync();
    try
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS "SchoolGrades" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_SchoolGrades" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL COLLATE NOCASE,
                "SortOrder" INTEGER NOT NULL DEFAULT 0,
                "IsActive" INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS "SchoolSections" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_SchoolSections" PRIMARY KEY AUTOINCREMENT,
                "GradeLevelId" INTEGER NOT NULL,
                "Name" TEXT NOT NULL COLLATE NOCASE,
                "SortOrder" INTEGER NOT NULL DEFAULT 0,
                "IsActive" INTEGER NOT NULL DEFAULT 1,
                CONSTRAINT "FK_SchoolSections_SchoolGrades_GradeLevelId"
                    FOREIGN KEY ("GradeLevelId") REFERENCES "SchoolGrades" ("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SchoolGrades_Name" ON "SchoolGrades" ("Name" COLLATE NOCASE);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SchoolSections_GradeLevelId_Name"
                ON "SchoolSections" ("GradeLevelId", "Name" COLLATE NOCASE);
            """;
        await command.ExecuteNonQueryAsync();

        foreach (var (table, column) in new[]
                 {
                     ("SchoolGrades", "IsActive"),
                     ("SchoolSections", "IsActive")
                 })
        {
            var tableColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var info = connection.CreateCommand())
            {
                info.CommandText = $"PRAGMA table_info('{table}');";
                await using var reader = await info.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    tableColumns.Add(reader.GetString(1));
            }
            if (tableColumns.Contains(column)) continue;
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" INTEGER NOT NULL DEFAULT 1;";
            await alter.ExecuteNonQueryAsync();
        }

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info('Users');";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(1));
        }

        foreach (var (name, declaration) in new[]
                 {
                     ("GradeLevelId", "INTEGER NULL REFERENCES SchoolGrades(Id) ON DELETE RESTRICT"),
                     ("SectionId", "INTEGER NULL REFERENCES SchoolSections(Id) ON DELETE RESTRICT")
                 })
        {
            if (columns.Contains(name)) continue;
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE \"Users\" ADD COLUMN \"{name}\" {declaration};";
            await alter.ExecuteNonQueryAsync();
        }
        await using var indexes = connection.CreateCommand();
        indexes.CommandText = """
            CREATE INDEX IF NOT EXISTS "IX_Users_GradeLevelId" ON "Users" ("GradeLevelId");
            CREATE INDEX IF NOT EXISTS "IX_Users_SectionId" ON "Users" ("SectionId");
            """;
        await indexes.ExecuteNonQueryAsync();
    }
    finally
    {
        if (openedHere) await connection.CloseAsync();
    }
}

static async Task EnsureReportSchemaAsync(AppDbContext db)
{
    var connection = db.Database.GetDbConnection();
    var openedHere = connection.State != System.Data.ConnectionState.Open;
    if (openedHere) await connection.OpenAsync();
    try
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS "Reports" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Reports" PRIMARY KEY AUTOINCREMENT,
                "UserId" INTEGER NOT NULL,
                "Title" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "Category" TEXT NOT NULL,
                "Location" TEXT NOT NULL,
                "Photo" BLOB NULL,
                "PhotoContentType" TEXT NULL,
                "Status" TEXT NOT NULL DEFAULT 'Pending',
                "Priority" TEXT NOT NULL DEFAULT 'Normal',
                "Assignee" TEXT NULL,
                "AdminNotes" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS "ReportActivities" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ReportActivities" PRIMARY KEY AUTOINCREMENT,
                "ReportId" INTEGER NOT NULL,
                "ActorUserId" INTEGER NOT NULL,
                "Action" TEXT NOT NULL,
                "Details" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                FOREIGN KEY ("ReportId") REFERENCES "Reports" ("Id") ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS "ReportNotifications" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ReportNotifications" PRIMARY KEY AUTOINCREMENT,
                "RecipientUserId" INTEGER NOT NULL,
                "ReportId" INTEGER NOT NULL,
                "Message" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "ReadAt" TEXT NULL,
                FOREIGN KEY ("ReportId") REFERENCES "Reports" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_Reports_UserId_CreatedAt" ON "Reports" ("UserId", "CreatedAt");
            CREATE INDEX IF NOT EXISTS "IX_ReportActivities_ReportId_CreatedAt" ON "ReportActivities" ("ReportId", "CreatedAt");
            CREATE INDEX IF NOT EXISTS "IX_ReportNotifications_RecipientUserId_CreatedAt" ON "ReportNotifications" ("RecipientUserId", "CreatedAt");
            """;
        await command.ExecuteNonQueryAsync();

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info('Reports');";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(1));
        }

        foreach (var (name, declaration) in new[]
                 {
                     ("PhotoContentType", "TEXT NULL"),
                     ("PublicUpdate", "TEXT NULL"),
                     ("Priority", "TEXT NOT NULL DEFAULT 'Normal'"),
                     ("Assignee", "TEXT NULL"),
                     ("IsReviewed", "INTEGER NOT NULL DEFAULT 0"),
                     ("ResolvedAt", "TEXT NULL")
                 })
        {
            if (columns.Contains(name)) continue;
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE \"Reports\" ADD COLUMN \"{name}\" {declaration};";
            await alter.ExecuteNonQueryAsync();
            if (name == "IsReviewed")
            {
                await using var backfill = connection.CreateCommand();
                backfill.CommandText = "UPDATE \"Reports\" SET \"IsReviewed\" = 1;";
                await backfill.ExecuteNonQueryAsync();
            }
            if (name == "ResolvedAt")
            {
                await using var backfill = connection.CreateCommand();
                backfill.CommandText = """
                    UPDATE "Reports"
                    SET "ResolvedAt" = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                    WHERE "Status" IN ('Resolved', 'Closed', 'Fixed') AND "ResolvedAt" IS NULL;
                    """;
                await backfill.ExecuteNonQueryAsync();
            }
        }
        await using var normalize = connection.CreateCommand();
        normalize.CommandText = """
            UPDATE "Reports" SET "Status" = CASE "Status"
                WHEN 'New' THEN 'Pending'
                WHEN 'Seen' THEN 'Acknowledged'
                WHEN 'In progress' THEN 'In Progress'
                WHEN 'Fixed' THEN 'Resolved'
                WHEN 'Closed' THEN 'Resolved'
                ELSE "Status" END
            WHERE "Status" IN ('New', 'Seen', 'In progress', 'Fixed', 'Closed');
            """;
        await normalize.ExecuteNonQueryAsync();
        await using var resolvedIndex = connection.CreateCommand();
        resolvedIndex.CommandText = """
            CREATE INDEX IF NOT EXISTS "IX_Reports_Status_ResolvedAt"
            ON "Reports" ("Status", "ResolvedAt");
            """;
        await resolvedIndex.ExecuteNonQueryAsync();
    }
    finally
    {
        if (openedHere) await connection.CloseAsync();
    }
}

static async Task EnsureAuthColumnsAsync(AppDbContext db)
{
    var connection = db.Database.GetDbConnection();
    var openedHere = connection.State != System.Data.ConnectionState.Open;
    if (openedHere) await connection.OpenAsync();

    try
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('Users');";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(1));
        }

        async Task AddColumnIfMissing(string name, string declaration)
        {
            if (columns.Contains(name)) return;
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE \"Users\" ADD COLUMN \"{name}\" {declaration};";
            await command.ExecuteNonQueryAsync();
            columns.Add(name);
        }

        await AddColumnIfMissing("Email", "TEXT");
        await AddColumnIfMissing("PasswordResetTokenHash", "TEXT");
        await AddColumnIfMissing("PasswordResetTokenExpiresUtc", "TEXT");
        await AddColumnIfMissing("Role", "TEXT NOT NULL DEFAULT 'user'");
        await AddColumnIfMissing("SessionVersion", "INTEGER NOT NULL DEFAULT 0");

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_Email"
                ON "Users" ("Email")
                WHERE "Email" IS NOT NULL AND "Email" <> '';
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_PasswordResetTokenHash"
                ON "Users" ("PasswordResetTokenHash")
                WHERE "PasswordResetTokenHash" IS NOT NULL;
                CREATE TABLE IF NOT EXISTS "PasswordResetCodes" (
                    "Email" TEXT NOT NULL CONSTRAINT "PK_PasswordResetCodes" PRIMARY KEY,
                    "CodeHash" BLOB NULL,
                    "CodeSalt" BLOB NULL,
                    "ExpiresUtc" TEXT NOT NULL,
                    "CreatedUtc" TEXT NOT NULL,
                    "Attempts" INTEGER NOT NULL,
                    "ResetGrantHash" BLOB NULL,
                    "ResetGrantExpiresUtc" TEXT NULL,
                    "UsedUtc" TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS "PendingRegistrations" (
                    "Email" TEXT NOT NULL CONSTRAINT "PK_PendingRegistrations" PRIMARY KEY,
                    "Username" TEXT NOT NULL,
                    "PasswordHash" TEXT NOT NULL,
                    "GradeLevelId" INTEGER NULL,
                    "SectionId" INTEGER NULL,
                    "CodeHash" BLOB NULL,
                    "CodeSalt" BLOB NULL,
                    "ExpiresUtc" TEXT NOT NULL,
                    "CreatedUtc" TEXT NOT NULL,
                    "Attempts" INTEGER NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_PendingRegistrations_Username"
                    ON "PendingRegistrations" ("Username");
                """;
            await command.ExecuteNonQueryAsync();
        }
    }
    finally
    {
        if (openedHere) await connection.CloseAsync();
    }
}

static async Task SendVerificationCodeEmailAsync(SmtpSettings settings, string toEmail, string code)
{
    using var message = new MailMessage
    {
        From = new MailAddress(settings.FromEmail!, settings.FromName),
        Subject = "Your fixIt verification code",
        Body = $"Your fixIt password-reset verification code is {code}.\n\nIt expires in 10 minutes and can only be used once. If you did not request this code, you can ignore this email."
    };
    message.To.Add(new MailAddress(toEmail));
    message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
        $"Your fixIt password-reset verification code is {code}.\n\nIt expires in 10 minutes and can only be used once. If you did not request this code, you can ignore this email.",
        Encoding.UTF8,
        "text/plain"));

    using var client = new SmtpClient(settings.Host!, settings.Port)
    {
        EnableSsl = true,
        UseDefaultCredentials = false,
        Credentials = new NetworkCredential(settings.Username, settings.Password),
        DeliveryMethod = SmtpDeliveryMethod.Network,
        Timeout = 15000
    };
    await client.SendMailAsync(message);
}

static async Task SendRegistrationVerificationCodeEmailAsync(
    SmtpSettings settings, string toEmail, string code, CancellationToken cancellationToken)
{
    var body = $"Your fixIt account verification code is {code}.\n\nIt expires in 10 minutes and can only be used once. If you did not request this code, you can ignore this email.";
    using var message = new MailMessage
    {
        From = new MailAddress(settings.FromEmail!, settings.FromName),
        Subject = "Verify your fixIt account",
        Body = body
    };
    message.To.Add(new MailAddress(toEmail));
    message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
        body, Encoding.UTF8, "text/plain"));

    using var client = new SmtpClient(settings.Host!, settings.Port)
    {
        EnableSsl = true,
        UseDefaultCredentials = false,
        Credentials = new NetworkCredential(settings.Username, settings.Password),
        DeliveryMethod = SmtpDeliveryMethod.Network,
        Timeout = 15000
    };
    await client.SendMailAsync(message, cancellationToken);
}

public record RegisterRequest(string? Username, string? Email, string? Password, string? ConfirmPassword, int? GradeLevelId, int? SectionId);
public record RegistrationEmailRequest(string? Email);
public record VerifyRegistrationRequest(string? Email, string? Code);
public record LoginRequest(string? Email, string? Password);
public record ForgotPasswordRequest(string? Email);
public record VerifyResetCodeRequest(string? Email, string? Code);
public record ResetPasswordRequest(string? ResetToken, string? Password, string? ConfirmPassword);
public record CreateReportRequest(string? Title, string? Description, string? Category, string? Location, string? Image);
public record UpdateReportRequest(string? Status, string? Priority, string? Assignee, string? AdminNotes, string? PublicUpdate);
public record UpdateProfileRequest(string? Username);
public record UpdateClassRequest(int? GradeLevelId, int? SectionId);
public record CreateClassFolderRequest(string? Name);
public record RenameClassFolderRequest(string? Name);
public record UpdateClassFolderStatusRequest(bool? IsActive);
public record GradeDirectoryResponse(int Id, string Name, List<SectionDirectoryResponse> Sections);
public record SectionDirectoryResponse(int Id, string Name, int GradeLevelId);
public record AdminGradeDirectoryResponse(
    int Id, string Name, bool IsActive, List<AdminSectionDirectoryResponse> Sections);
public record AdminSectionDirectoryResponse(int Id, string Name, int GradeLevelId, bool IsActive);
public record ClassOptionImportRow(string GradeLevel, string Section);
public record ClassOptionEntry(string GradeLevel, string Section);
public record ClassOptionsImportRequest(IReadOnlyCollection<ClassOptionImportRow>? Rows, string? Snapshot);
public record ClassOptionsPreview(
    IReadOnlyList<ClassOptionImportRow> Rows,
    IReadOnlyList<string> GradesToAdd,
    IReadOnlyList<string> GradesToRestore,
    IReadOnlyList<ClassOptionUpdate> GradesToUpdate,
    IReadOnlyList<string> GradesToRetain,
    IReadOnlyList<ClassOptionRemoval> GradesToDelete,
    IReadOnlyList<ClassOptionRemoval> GradesToArchive,
    IReadOnlyList<ClassOptionEntry> SectionsToAdd,
    IReadOnlyList<ClassOptionEntry> SectionsToRestore,
    IReadOnlyList<ClassOptionUpdate> SectionsToUpdate,
    IReadOnlyList<ClassOptionEntry> SectionsToRetain,
    IReadOnlyList<ClassOptionRemoval> SectionsToDelete,
    IReadOnlyList<ClassOptionRemoval> SectionsToArchive,
    string Snapshot);
public record ClassOptionsValidation(List<ClassOptionImportRow> Rows, List<string> Errors);
public record ClassOptionUpdate(string? GradeLevel, string CurrentName, string Name);
public record ClassOptionRemoval(string GradeLevel, string Name, int StudentCount, int ReportCount);
public record ClassOptionDependency(int StudentCount, int ReportCount)
{
    public bool HasDependencies => StudentCount > 0 || ReportCount > 0;
}
public record ClassOptionsImportResult(
    int GradesAdded,
    int GradesRestored,
    int GradesDeleted,
    int GradesArchived,
    int SectionsAdded,
    int SectionsRestored,
    int SectionsDeleted,
    int SectionsArchived);

public sealed class ClassOptionsImportException(string message, IReadOnlyList<string>? errors = null)
    : Exception(message)
{
    public IReadOnlyList<string> Errors { get; } = errors ?? Array.Empty<string>();
}

public sealed class SmtpSettings
{
    public string? Host { get; init; }
    public int Port { get; init; } = 587;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? FromEmail { get; init; }
    public string FromName { get; init; } = "fixIt";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) &&
        Port > 0 &&
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(Password) &&
        !string.IsNullOrWhiteSpace(FromEmail);

    public static SmtpSettings FromConfiguration(IConfiguration config)
    {
        _ = int.TryParse(config["SMTP:PORT"], out var port);
        return new SmtpSettings
        {
            Host = config["SMTP:HOST"],
            Port = port > 0 ? port : 587,
            Username = config["SMTP:USERNAME"],
            Password = config["SMTP:PASSWORD"],
            FromEmail = config["SMTP:FROMEMAIL"] ?? config["SMTP:USERNAME"],
            FromName = config["SMTP:FROMNAME"] ?? "fixIt"
        };
    }
}

public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string? Email { get; set; }
    public string Role { get; set; } = "user";
    public string? PasswordResetTokenHash { get; set; }
    public DateTime? PasswordResetTokenExpiresUtc { get; set; }
    public int SessionVersion { get; set; }
    public int? GradeLevelId { get; set; }
    public int? SectionId { get; set; }
}

public class SchoolGrade
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class SchoolSection
{
    public int Id { get; set; }
    public int GradeLevelId { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class PasswordResetCode
{
    public string Email { get; set; } = "";
    public byte[]? CodeHash { get; set; }
    public byte[]? CodeSalt { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public int Attempts { get; set; }
    public byte[]? ResetGrantHash { get; set; }
    public DateTime? ResetGrantExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
}

public class PendingRegistration
{
    public string Email { get; set; } = "";
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public int? GradeLevelId { get; set; }
    public int? SectionId { get; set; }
    public byte[]? CodeHash { get; set; }
    public byte[]? CodeSalt { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public int Attempts { get; set; }
}

public class MaintenanceReport
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string Location { get; set; } = "";
    public byte[]? Photo { get; set; }
    public string? PhotoContentType { get; set; }
    public string Status { get; set; } = "Pending";
    public string Priority { get; set; } = "Normal";
    public bool IsReviewed { get; set; }
    public string? Assignee { get; set; }
    public string? PublicUpdate { get; set; }
    public string? AdminNotes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public class ReportActivity
{
    public int Id { get; set; }
    public int ReportId { get; set; }
    public int ActorUserId { get; set; }
    public string Action { get; set; } = "";
    public string Details { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ReportNotification
{
    public int Id { get; set; }
    public int RecipientUserId { get; set; }
    public int ReportId { get; set; }
    public string Message { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<SchoolGrade> SchoolGrades => Set<SchoolGrade>();
    public DbSet<SchoolSection> SchoolSections => Set<SchoolSection>();
    public DbSet<PasswordResetCode> PasswordResetCodes => Set<PasswordResetCode>();
    public DbSet<PendingRegistration> PendingRegistrations => Set<PendingRegistration>();
    public DbSet<MaintenanceReport> Reports => Set<MaintenanceReport>();
    public DbSet<ReportActivity> ReportActivities => Set<ReportActivity>();
    public DbSet<ReportNotification> ReportNotifications => Set<ReportNotification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<AppUser>();
        user.HasIndex(x => x.Username).IsUnique();
        user.HasIndex(x => x.Email).IsUnique()
            .HasFilter("\"Email\" IS NOT NULL AND \"Email\" <> ''");
        user.HasIndex(x => x.PasswordResetTokenHash).IsUnique()
            .HasFilter("\"PasswordResetTokenHash\" IS NOT NULL");
        user.HasIndex(x => x.GradeLevelId);
        user.HasIndex(x => x.SectionId);

        var pendingRegistration = modelBuilder.Entity<PendingRegistration>();
        pendingRegistration.HasKey(item => item.Email);
        pendingRegistration.HasIndex(item => item.Username).IsUnique();

        var grade = modelBuilder.Entity<SchoolGrade>();
        grade.Property(item => item.Name).UseCollation("NOCASE");
        grade.HasIndex(item => item.Name).IsUnique();

        var section = modelBuilder.Entity<SchoolSection>();
        section.Property(item => item.Name).UseCollation("NOCASE");
        section.HasIndex(item => new { item.GradeLevelId, item.Name }).IsUnique();
        section.HasOne<SchoolGrade>().WithMany().HasForeignKey(item => item.GradeLevelId)
            .OnDelete(DeleteBehavior.Restrict);

        user.HasOne<SchoolGrade>().WithMany().HasForeignKey(item => item.GradeLevelId)
            .OnDelete(DeleteBehavior.Restrict);
        user.HasOne<SchoolSection>().WithMany().HasForeignKey(item => item.SectionId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PasswordResetCode>().HasKey(x => x.Email);
        modelBuilder.Entity<MaintenanceReport>().ToTable("Reports");
        modelBuilder.Entity<ReportActivity>().ToTable("ReportActivities");
        modelBuilder.Entity<ReportNotification>().ToTable("ReportNotifications");
    }
}

public sealed class ResolvedReportCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<ResolvedReportCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DeleteExpiredReportsAsync(stoppingToken);
            }
            catch (SqliteException exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Could not query expired reports during scheduled cleanup.");
            }
            await Task.Delay(CleanupInterval, stoppingToken);
        }
    }

    private async Task DeleteExpiredReportsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cutoff = DateTime.UtcNow.AddHours(-72);
        var expiredReports = await db.Reports
            .Where(report => report.Status == "Resolved" && report.ResolvedAt != null && report.ResolvedAt <= cutoff)
            .ToListAsync(cancellationToken);

        foreach (var report in expiredReports)
        {
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                db.ReportActivities.RemoveRange(db.ReportActivities.Where(item => item.ReportId == report.Id));
                db.ReportNotifications.RemoveRange(db.ReportNotifications.Where(item => item.ReportId == report.Id));
                db.Reports.Remove(report);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is DbUpdateException or SqliteException)
            {
                logger.LogError(exception, "Could not delete resolved report {ReportId} during scheduled cleanup.", report.Id);
                db.ChangeTracker.Clear();
            }
        }
    }
}
