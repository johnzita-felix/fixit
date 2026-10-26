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
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

var databasePath = Path.Combine(builder.Environment.ContentRootPath, "app.db");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();

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
        new Claim(ClaimTypes.Email, user.Email ?? "")
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));

    return Results.Ok(new { message = "Login successful!", redirectUrl = "dashboard_user.html" });
}).RequireRateLimiting("auth");

app.MapPost("/api/forgot-password", async (
    ForgotPasswordRequest request,
    AppDbContext db,
    IConfiguration config,
    IWebHostEnvironment environment,
    ILogger<Program> logger,
    HttpContext context) =>
{
    var email = NormalizeEmail(request.Email);
    if (string.IsNullOrWhiteSpace(email) || !IsValidEmail(email))
        return Results.BadRequest(new { message = "Enter a valid email address." });

    var smtp = SmtpSettings.FromConfiguration(config);
    if (!smtp.IsConfigured && !environment.IsDevelopment())
    {
        return Results.Problem(
            title: "Email recovery is not configured",
            detail: "The server administrator must configure SMTP email settings before password recovery can be used.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var genericMessage = "If an account with that email exists, reset instructions will be sent shortly.";
    var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
    if (user is null)
        return Results.Ok(new { message = genericMessage });

    var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    user.PasswordResetTokenHash = HashToken(token);
    user.PasswordResetTokenExpiresUtc = DateTime.UtcNow.AddMinutes(30);
    await db.SaveChangesAsync();

    var configuredBaseUrl = config["App:PublicBaseUrl"]?.TrimEnd('/');
    var baseUrl = !string.IsNullOrWhiteSpace(configuredBaseUrl)
        ? configuredBaseUrl
        : $"{context.Request.Scheme}://{context.Request.Host}";
    var resetUrl = $"{baseUrl}/resetpw.html?token={Uri.EscapeDataString(token)}";

    if (!smtp.IsConfigured)
    {
        // Development-only fallback. Never log reset tokens in production.
        logger.LogWarning("SMTP is not configured. Development reset link (valid for 30 minutes): {ResetUrl}", resetUrl);
        return Results.Ok(new
        {
            message = "For local testing, check the server terminal for the reset link. Configure SMTP to deliver reset links by email."
        });
    }

    try
    {
        await SendResetEmailAsync(smtp, email, resetUrl);
        return Results.Ok(new { message = genericMessage });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to send a fixIt password reset email.");
        user.PasswordResetTokenHash = null;
        user.PasswordResetTokenExpiresUtc = null;
        await db.SaveChangesAsync();
        return Results.Problem(
            title: "Unable to send reset email",
            detail: "Email delivery failed. Check the SMTP settings and try again.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireRateLimiting("auth");

app.MapPost("/api/reset-password", async (
    ResetPasswordRequest request,
    AppDbContext db,
    IPasswordHasher<AppUser> hasher) =>
{
    if (string.IsNullOrWhiteSpace(request.Token))
        return Results.BadRequest(new { message = "This reset link is invalid or expired. Request a new one." });

    if (string.IsNullOrEmpty(request.Password) || request.Password.Length < 8 || request.Password.Length > 128)
        return Results.BadRequest(new { message = "Password must contain 8–128 characters." });

    if (request.Password != request.ConfirmPassword)
        return Results.BadRequest(new { message = "Passwords do not match." });

    var tokenHash = HashToken(request.Token);
    var user = await db.Users.SingleOrDefaultAsync(u => u.PasswordResetTokenHash == tokenHash);
    if (user is null ||
        user.PasswordResetTokenExpiresUtc is null ||
        user.PasswordResetTokenExpiresUtc.Value <= DateTime.UtcNow)
        return Results.BadRequest(new { message = "This reset link is invalid or expired. Request a new one." });

    user.PasswordHash = hasher.HashPassword(user, request.Password);
    user.PasswordResetTokenHash = null;
    user.PasswordResetTokenExpiresUtc = null;
    await db.SaveChangesAsync();

    return Results.Ok(new { message = "Password updated successfully. You can now sign in." });
}).RequireRateLimiting("auth");

app.MapGet("/api/me", (HttpContext context) =>
    Results.Ok(new
    {
        username = context.User.Identity?.Name,
        email = context.User.FindFirstValue(ClaimTypes.Email)
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

        async Task AddColumnIfMissing(string name, string type)
        {
            if (columns.Contains(name)) return;
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE \"Users\" ADD COLUMN \"{name}\" {type} NULL;";
            await command.ExecuteNonQueryAsync();
            columns.Add(name);
        }

        await AddColumnIfMissing("Email", "TEXT");
        await AddColumnIfMissing("PasswordResetTokenHash", "TEXT");
        await AddColumnIfMissing("PasswordResetTokenExpiresUtc", "TEXT");

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_Email"
                ON "Users" ("Email")
                WHERE "Email" IS NOT NULL AND "Email" <> '';
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_PasswordResetTokenHash"
                ON "Users" ("PasswordResetTokenHash")
                WHERE "PasswordResetTokenHash" IS NOT NULL;
                """;
            await command.ExecuteNonQueryAsync();
        }
    }
    finally
    {
        if (openedHere) await connection.CloseAsync();
    }
}

static async Task SendResetEmailAsync(SmtpSettings settings, string toEmail, string resetUrl)
{
    using var message = new MailMessage
    {
        From = new MailAddress(settings.FromEmail!, settings.FromName),
        Subject = "Reset your fixIt password",
        IsBodyHtml = true,
        Body = $"""
            <!doctype html>
            <html><body style="font-family:Arial,sans-serif;color:#1f2937;line-height:1.6">
              <div style="max-width:560px;margin:24px auto;padding:28px;border:1px solid #e5e7eb;border-radius:14px">
                <h1 style="color:#123c67;margin-top:0">fixIt</h1>
                <h2>Password reset request</h2>
                <p>We received a request to reset the password for your fixIt account.</p>
                <p><a href="{WebUtility.HtmlEncode(resetUrl)}" style="display:inline-block;background:#123c67;color:#fff;padding:12px 18px;border-radius:8px;text-decoration:none">Choose a new password</a></p>
                <p>This link expires in <strong>30 minutes</strong> and can only be used once.</p>
                <p>If you did not request this, you can ignore this email. Your password will not change.</p>
              </div>
            </body></html>
            """
    };
    message.To.Add(new MailAddress(toEmail));
    message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
        $"We received a request to reset your fixIt password. Open this link within 30 minutes: {resetUrl}\nIf you did not request this, ignore this email.",
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
public record ResetPasswordRequest(string? Token, string? Password, string? ConfirmPassword);

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
    public string? PasswordResetTokenHash { get; set; }
    public DateTime? PasswordResetTokenExpiresUtc { get; set; }
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<AppUser>();
        user.HasIndex(x => x.Username).IsUnique();
        user.HasIndex(x => x.Email).IsUnique()
            .HasFilter("\"Email\" IS NOT NULL AND \"Email\" <> ''");
        user.HasIndex(x => x.PasswordResetTokenHash).IsUnique()
            .HasFilter("\"PasswordResetTokenHash\" IS NOT NULL");
    }
}
