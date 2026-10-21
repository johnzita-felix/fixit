using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var databasePath = Path.Combine(builder.Environment.ContentRootPath, "app.db");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(2);
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = 401;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = 403;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Create tables for a new database and safely add Email to an existing Users table.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    var connection = db.Database.GetDbConnection();
    await connection.OpenAsync();
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "PRAGMA table_info('Users');";
        using var reader = await command.ExecuteReaderAsync();
        var hasEmail = false;
        while (await reader.ReadAsync())
            if (reader.GetString(1).Equals("Email", StringComparison.OrdinalIgnoreCase))
                hasEmail = true;
        await reader.DisposeAsync();
        if (!hasEmail)
        {
            command.CommandText = "ALTER TABLE Users ADD COLUMN Email TEXT NULL;";
            await command.ExecuteNonQueryAsync();
        }
        command.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_Email ON Users(Email) WHERE Email IS NOT NULL;";
        await command.ExecuteNonQueryAsync();
    }
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/api/register", async (RegisterRequest request, AppDbContext db,
    IPasswordHasher<AppUser> hasher) =>
{
    var username = request.Username?.Trim();
    var email = request.Email?.Trim().ToLowerInvariant();
    var password = request.Password;

    if (string.IsNullOrWhiteSpace(username) || username.Length < 3 || username.Length > 50)
        return Results.BadRequest(new { message = "Name must contain 3–50 characters." });
    if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || email.Length > 254)
        return Results.BadRequest(new { message = "Enter a valid email address." });
    if (string.IsNullOrEmpty(password) || password.Length < 8 || password.Length > 128)
        return Results.BadRequest(new { message = "Password must contain 8–128 characters." });
    if (password != request.ConfirmPassword)
        return Results.BadRequest(new { message = "Passwords do not match." });
    if (await db.Users.AnyAsync(u => u.Email == email))
        return Results.Conflict(new { message = "An account with this email already exists." });

    var user = new AppUser { Username = username, Email = email };
    user.PasswordHash = hasher.HashPassword(user, password);
    db.Users.Add(user);
    try { await db.SaveChangesAsync(); }
    catch (DbUpdateException)
    {
        return Results.Conflict(new { message = "An account with this email already exists." });
    }
    return Results.Ok(new { message = "Account created. You can now sign in." });
});

app.MapPost("/api/login", async (LoginRequest request, AppDbContext db,
    IPasswordHasher<AppUser> hasher, HttpContext context) =>
{
    var email = request.Email?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { message = "Enter your email and password." });

    var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
    if (user is null || string.IsNullOrEmpty(user.PasswordHash) ||
        hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        return Results.Json(new { message = "Invalid email or password." }, statusCode: 401);

    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.Email, user.Email ?? "")
    };
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok(new { message = "Login successful.", redirectUrl = "/index.html" });
});

app.MapGet("/api/me", (HttpContext context) =>
    Results.Ok(new { username = context.User.Identity!.Name }))
    .RequireAuthorization();

app.MapPost("/api/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { message = "Logged out." });
}).RequireAuthorization();

// This endpoint only acknowledges a request. Email delivery/token-based reset is not configured yet.
app.MapPost("/api/forgot-password", async (ForgotPasswordRequest request, AppDbContext db) =>
{
    var email = request.Email?.Trim().ToLowerInvariant();
    if (!string.IsNullOrWhiteSpace(email))
        _ = await db.Users.AnyAsync(u => u.Email == email);
    return Results.Ok(new { message = "If an account exists for that email, password-reset instructions can be sent once email delivery is configured." });
});

app.Run();

public record RegisterRequest(string? Username, string? Email, string? Password, string? ConfirmPassword);
public record LoginRequest(string? Email, string? Password);
public record ForgotPasswordRequest(string? Email);

public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = "";
}
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<AppUser> Users => Set<AppUser>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>().HasIndex(u => u.Email).IsUnique()
            .HasFilter("\"Email\" IS NOT NULL");
    }
}
