using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// SQLite database file, stored outside wwwroot.
var databasePath = Path.Combine(
    builder.Environment.ContentRootPath,
    "app.db"
);

// Register the database connection.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}")
);

// Register password hashing.
builder.Services.AddScoped<
    IPasswordHasher<AppUser>,
    PasswordHasher<AppUser>
>();

// Register cookie authentication.
builder.Services
    .AddAuthentication(
        CookieAuthenticationDefaults.AuthenticationScheme
    )
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

// Create the database and Users table on the first run.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider
        .GetRequiredService<AppDbContext>();

    await db.Database.EnsureCreatedAsync();

    // Create a sample account for local development only.
    if (app.Environment.IsDevelopment() &&
        !await db.Users.AnyAsync(u => u.Username == "lester"))
    {
        var hasher = scope.ServiceProvider
            .GetRequiredService<IPasswordHasher<AppUser>>();

        var user = new AppUser
        {
            Username = "lester"
        };

        user.PasswordHash = hasher.HashPassword(
            user,
            "PracticeOnly!123"
        );

        db.Users.Add(user);

        await db.SaveChangesAsync();
    }
}

app.UseHttpsRedirection();

// Opens wwwroot/index.html at the root URL.
app.UseDefaultFiles();

// Serves CSS, JavaScript, images, and other public files.
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// LOGIN ENDPOINT
app.MapPost("/api/login", async (
    LoginRequest request,
    AppDbContext db,
    IPasswordHasher<AppUser> hasher,
    HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) ||
        string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new
        {
            message = "Enter your username and password."
        });
    }

    var username = request.Username.Trim();

    // Find the user in the Users table.
    var user = await db.Users.SingleOrDefaultAsync(
        u => u.Username == username
    );

    if (user is null)
    {
        return Results.Json(
            new
            {
                message = "Invalid username or password."
            },
            statusCode: 401
        );
    }

    // Compare the submitted password with the saved hash.
    var result = hasher.VerifyHashedPassword(
        user,
        user.PasswordHash,
        request.Password
    );

    if (result == PasswordVerificationResult.Failed)
    {
        return Results.Json(
            new
            {
                message = "Invalid username or password."
            },
            statusCode: 401
        );
    }

    // Upgrade the password hash when needed.
    if (result ==
        PasswordVerificationResult.SuccessRehashNeeded)
    {
        user.PasswordHash = hasher.HashPassword(
            user,
            request.Password
        );

        await db.SaveChangesAsync();
    }

    // Information stored in the authentication cookie.
    var claims = new[]
    {
        new Claim(
            ClaimTypes.NameIdentifier,
            user.Id.ToString()
        ),
        new Claim(
            ClaimTypes.Name,
            user.Username
        )
    };

    var identity = new ClaimsIdentity(
        claims,
        CookieAuthenticationDefaults.AuthenticationScheme
    );

    // Sign in the user by creating an authentication cookie.
    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity)
    );

    return Results.Ok(new
    {
        message = "Login successful!"
    });
});

// CURRENT USER ENDPOINT
// Accessible only to signed-in users.
app.MapGet("/api/me", (HttpContext context) =>
{
    return Results.Ok(new
    {
        username = context.User.Identity!.Name
    });
}).RequireAuthorization();

// LOGOUT ENDPOINT
app.MapPost("/api/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(
        CookieAuthenticationDefaults.AuthenticationScheme
    );

    return Results.Ok(new
    {
        message = "Logged out."
    });
}).RequireAuthorization();

app.MapPost("/api/register", async (
    RegisterRequest request,
    AppDbContext db,
    IPasswordHasher<AppUser> hasher) =>
{
    var username = request.Username?.Trim();
    var password = request.Password;

    // Validate the submitted data.
    if (string.IsNullOrWhiteSpace(username) ||
        string.IsNullOrWhiteSpace(password))
    {
        return Results.BadRequest(new
        {
            message = "Username and password are required."
        });
    }

    if (username.Length < 3 || username.Length > 50)
    {
        return Results.BadRequest(new
        {
            message = "Username must contain 3–50 characters."
        });
    }

    if (password.Length < 12 || password.Length > 128)
    {
        return Results.BadRequest(new
        {
            message = "Password must contain 12–128 characters."
        });
    }

    if (password != request.ConfirmPassword)
    {
        return Results.BadRequest(new
        {
            message = "Passwords do not match."
        });
    }

    // Check whether the username already exists.
    if (await db.Users.AnyAsync(u => u.Username == username))
    {
        return Results.Conflict(new
        {
            message = "Username is already taken."
        });
    }

    var user = new AppUser
    {
        Username = username
    };

    // Save a hash, never the original password.
    user.PasswordHash = hasher.HashPassword(user, password);

    db.Users.Add(user);

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException ex) when (
        ex.InnerException is Microsoft.Data.Sqlite.SqliteException
        {
            SqliteExtendedErrorCode: 2067
        })
    {
        // Handles two requests registering the same username.
        return Results.Conflict(new
        {
            message = "Username is already taken."
        });
    }

    return Results.Ok(new
    {
        message = "Account created! You can now log in."
    });
});

app.Run();

public record RegisterRequest(
    string? Username,
    string? Password,
    string? ConfirmPassword
);

// Defines the login data received from JavaScript.
public record LoginRequest(
    string? Username,
    string? Password
);

// Defines the columns in the Users table.
public class AppUser
{
    public int Id { get; set; }

    public string Username { get; set; } = "";

    public string PasswordHash { get; set; } = "";
}

// Defines the database and its tables.
public class AppDbContext : DbContext
{
    public AppDbContext(
        DbContextOptions<AppDbContext> options
    ) : base(options)
    {
    }

    public DbSet<AppUser> Users => Set<AppUser>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    )
    {
        // Prevent duplicate usernames.
        modelBuilder.Entity<AppUser>()
            .HasIndex(user => user.Username)
            .IsUnique();
    }
}