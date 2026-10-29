using System.Net;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

var databasePath = Path.Combine(builder.Environment.ContentRootPath, "app.db");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddMemoryCache();

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
            if (!int.TryParse(userId, out var parsedUserId) ||
                !int.TryParse(sessionVersion, out var parsedSessionVersion))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

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
    IPasswordHasher<AppUser> hasher) =>
{
    var username = request.Username?.Trim();
    var email = NormalizeEmail(request.Email);
    var password = request.Password;

    if (string.IsNullOrWhiteSpace(username) ||
        string.IsNullOrWhiteSpace(email) ||
        string.IsNullOrWhiteSpace(password))
        return Results.BadRequest(new { message = "Name, email, and password are required." });

    if (username.Length < 3 || username.Length > 50)
        return Results.BadRequest(new { message = "Name must contain 3–50 characters." });

    if (!IsValidEmail(email))
        return Results.BadRequest(new { message = "Enter a valid email address." });

    if (password.Length < 8 || password.Length > 128)
        return Results.BadRequest(new { message = "Password must contain 8–128 characters." });

    if (password != request.ConfirmPassword)
        return Results.BadRequest(new { message = "Passwords do not match." });

    if (await db.Users.AnyAsync(u => u.Username == username))
        return Results.Conflict(new { message = "That name is already registered. Try a different name." });

    if (await db.Users.AnyAsync(u => u.Email == email))
        return Results.Conflict(new { message = "An account with that email already exists." });

    var user = new AppUser { Username = username, Email = email };
    user.PasswordHash = hasher.HashPassword(user, password);
    db.Users.Add(user);

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqlite &&
                                        (sqlite.SqliteExtendedErrorCode == 2067 ||
                                         sqlite.SqliteExtendedErrorCode == 1555))
    {
        return Results.Conflict(new { message = "That name or email is already registered." });
    }

    return Results.Ok(new { message = "Account created! You can now sign in." });
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
    IConfiguration config,
    IMemoryCache memoryCache,
    HttpContext context) =>
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
    var cooldownKey = $"password-reset:{HashToken(email)}";
    if (memoryCache.TryGetValue(cooldownKey, out _))
        return Results.Ok(new { message = genericMessage, expiresInSeconds = codeLifetimeSeconds });

    memoryCache.Set(cooldownKey, true, TimeSpan.FromSeconds(60));
    var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
    if (user is null)
        return Results.Ok(new { message = genericMessage, expiresInSeconds = codeLifetimeSeconds });

    var now = DateTime.UtcNow;
    var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    var salt = RandomNumberGenerator.GetBytes(16);
    var resetCode = await db.PasswordResetCodes.SingleOrDefaultAsync(item => item.Email == email);
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
        await db.SaveChangesAsync();
        memoryCache.Remove(cooldownKey);
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

app.MapGet("/api/me", (HttpContext context) =>
    Results.Ok(new
    {
        username = context.User.Identity?.Name,
        email = context.User.FindFirstValue(ClaimTypes.Email),
        role = context.User.FindFirstValue(ClaimTypes.Role) ?? "user"
    })).RequireAuthorization();

app.MapPost("/api/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { message = "Logged out." });
}).RequireAuthorization();

app.Run();

static string? NormalizeEmail(string? email) =>
    string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

static bool IsValidEmail(string email)
{
    try
    {
        var address = new MailAddress(email);
        return string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase);
    }
    catch { return false; }
}

static string HashToken(string token) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

static byte[] HashVerificationCode(string code, byte[] salt) =>
    Rfc2898DeriveBytes.Pbkdf2(code, salt, 210_000, HashAlgorithmName.SHA256, 32);

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

public record RegisterRequest(string? Username, string? Email, string? Password, string? ConfirmPassword);
public record LoginRequest(string? Email, string? Password);
public record ForgotPasswordRequest(string? Email);
public record VerifyResetCodeRequest(string? Email, string? Code);
public record ResetPasswordRequest(string? ResetToken, string? Password, string? ConfirmPassword);

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

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<PasswordResetCode> PasswordResetCodes => Set<PasswordResetCode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<AppUser>();
        user.HasIndex(x => x.Username).IsUnique();
        user.HasIndex(x => x.Email).IsUnique()
            .HasFilter("\"Email\" IS NOT NULL AND \"Email\" <> ''");
        user.HasIndex(x => x.PasswordResetTokenHash).IsUnique()
            .HasFilter("\"PasswordResetTokenHash\" IS NOT NULL");

        modelBuilder.Entity<PasswordResetCode>().HasKey(x => x.Email);
    }
}
