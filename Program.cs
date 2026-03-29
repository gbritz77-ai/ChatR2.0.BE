// Program.cs
using System.Text;
using Amazon;
using Amazon.S3;
using Chat.Api.Data;
using Chat.Api.Hubs;
using Chat.Api.Models;
using Chat.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// ---------- DbContext ----------
builder.Services.AddDbContext<ChatDbContext>(options =>
{
    var cs = builder.Configuration.GetConnectionString("DefaultConnection")
             ?? throw new InvalidOperationException("DefaultConnection is missing.");

    options.UseMySql(cs, new MySqlServerVersion(new Version(8, 0, 36)));
});

// ---------- CORS ----------
const string FrontendPolicy = "FrontendPolicy";

builder.Services.AddCors(options =>
{
    options.AddPolicy(FrontendPolicy, policy =>
    {
        policy
            .WithOrigins(
                "http://localhost:5173",
                "https://main.d1imfsef8qotjc.amplifyapp.com",
                "https://d1gnxnjelgzuho.cloudfront.net"
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// ---------- S3 (FIXED) ----------
// IMPORTANT:
// - Do NOT call FallbackCredentialsFactory.GetCredentials() in ECS/Fargate.
// - Let the AWS SDK automatically use the ECS Task Role credentials.
// - Ensure your ECS task definition has *taskRoleArn* with S3 permissions.
builder.Services.AddSingleton<IAmazonS3>(_ =>
{
    var regionName =
        Environment.GetEnvironmentVariable("AWS_REGION")
        ?? builder.Configuration["AWS:Region"]
        ?? builder.Configuration["AWS__Region"]
        ?? "eu-west-2";

    var region = RegionEndpoint.GetBySystemName(regionName);

    // Uses default credential chain (ECS task role, env vars, etc.)
    return new AmazonS3Client(region);
});

// ---------- Email service ----------
builder.Services.AddSingleton<IEmailService, EmailService>();

// ---------- Controllers & SignalR ----------
builder.Services.AddSingleton<Chat.Api.Services.CallManager>();
builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddSignalR();
builder.Services.AddHttpContextAccessor();

// ---------- Forwarded headers (behind ALB/CloudFront) ----------
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// ---------- JWT Auth ----------
var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection["Key"];
var jwtIssuer = jwtSection["Issuer"];
var jwtAudience = jwtSection["Audience"];

if (string.IsNullOrWhiteSpace(jwtKey) ||
    string.IsNullOrWhiteSpace(jwtIssuer) ||
    string.IsNullOrWhiteSpace(jwtAudience))
{
    throw new InvalidOperationException("JWT config missing: Jwt:Key/Issuer/Audience");
}

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/chat"))
                    context.Token = accessToken;

                return Task.CompletedTask;
            }
        };

        options.RequireHttpsMetadata = false;
        options.SaveToken = true;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

// ---------- Swagger ----------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Chat API", Version = "v1" });

    var securityScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Description = "Enter 'Bearer {token}'",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    };

    c.AddSecurityDefinition("Bearer", securityScheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Apply any pending EF Core migrations on startup.
// The DB was originally created without EF migration tracking, so InitialCreate
// is not recorded in __EFMigrationsHistory even though the tables already exist.
// We mark it as applied first so Migrate() only runs the newer migrations.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

    // Stamp InitialCreate as applied so Migrate() skips it (DB predates EF tracking)
    db.Database.ExecuteSqlRaw(
        "INSERT IGNORE INTO `__EFMigrationsHistory` (MigrationId, ProductVersion) " +
        "VALUES ('20260123125355_InitialCreate', '8.0.0')");

    db.Database.Migrate();

    // AddUserAvailability: stamp the migration as applied and add columns via raw
    // ADO.NET so we can check INFORMATION_SCHEMA first (IF NOT EXISTS not supported
    // on this MySQL version; migrationBuilder.Sql multi-statements also unreliable).
    db.Database.ExecuteSqlRaw(
        "INSERT IGNORE INTO `__EFMigrationsHistory` (MigrationId, ProductVersion) " +
        "VALUES ('20260318080000_AddUserAvailability', '8.0.0')");

    var conn = db.Database.GetDbConnection();
    conn.Open();
    void AddColumnIfMissing(string column, string definition)
    {
        using var check = conn.CreateCommand();
        check.CommandText =
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Users' AND COLUMN_NAME = @col";
        var p = check.CreateParameter(); p.ParameterName = "@col"; p.Value = column;
        check.Parameters.Add(p);
        var exists = Convert.ToInt64(check.ExecuteScalar()) > 0;
        if (!exists)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE Users ADD COLUMN {definition}";
            alter.ExecuteNonQuery();
        }
    }
    AddColumnIfMissing("AvailabilityDays", "AvailabilityDays varchar(50) NULL");
    AddColumnIfMissing("AvailabilityFrom", "AvailabilityFrom varchar(10) NULL");
    AddColumnIfMissing("AvailabilityTo",   "AvailabilityTo varchar(10) NULL");

    // AddMustChangePassword migration
    db.Database.ExecuteSqlRaw(
        "INSERT IGNORE INTO `__EFMigrationsHistory` (MigrationId, ProductVersion) " +
        "VALUES ('20260318090000_AddMustChangePassword', '8.0.0')");
    AddColumnIfMissing("MustChangePassword", "MustChangePassword tinyint(1) NOT NULL DEFAULT 0");

    // AddAvatarKey migration
    db.Database.ExecuteSqlRaw(
        "INSERT IGNORE INTO `__EFMigrationsHistory` (MigrationId, ProductVersion) " +
        "VALUES ('20260318110000_AddAvatarKey', '8.0.0')");
    AddColumnIfMissing("AvatarKey", "AvatarKey varchar(500) NULL");

    // AddMessageEditing migration — use conn directly (avoid conflict with open connection)
    try
    {
        using (var histCmd = conn.CreateCommand())
        {
            histCmd.CommandText =
                "INSERT IGNORE INTO `__EFMigrationsHistory` (MigrationId, ProductVersion) " +
                "VALUES ('20260320000000_AddMessageEditing', '8.0.0')";
            histCmd.ExecuteNonQuery();
        }
        void AddMessageColumnIfMissing(string column, string definition)
        {
            using var check = conn.CreateCommand();
            check.CommandText =
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Messages' AND COLUMN_NAME = @col";
            var p = check.CreateParameter(); p.ParameterName = "@col"; p.Value = column;
            check.Parameters.Add(p);
            var exists = Convert.ToInt64(check.ExecuteScalar()) > 0;
            if (!exists)
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE Messages ADD COLUMN {definition}";
                alter.ExecuteNonQuery();
            }
        }
        AddMessageColumnIfMissing("IsEdited", "IsEdited tinyint(1) NOT NULL DEFAULT 0");
        AddMessageColumnIfMissing("EditedAt", "EditedAt datetime NULL");
        AddMessageColumnIfMissing("ReplyToMessageId", "ReplyToMessageId char(36) NULL");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("[Migration] AddMessageEditing skipped: {Error}", ex.Message);
    }

    // AddUserGroup migration
    db.Database.ExecuteSqlRaw(
        "INSERT IGNORE INTO `__EFMigrationsHistory` (MigrationId, ProductVersion) " +
        "VALUES ('20260323000000_AddUserGroup', '8.0.0')");
    AddColumnIfMissing("Group", "`Group` varchar(100) NULL");

    // AddGroupAvatarKey migration — AvatarKey column on Chats table
    try
    {
        using (var histCmd = conn.CreateCommand())
        {
            histCmd.CommandText =
                "INSERT IGNORE INTO `__EFMigrationsHistory` (MigrationId, ProductVersion) " +
                "VALUES ('20260323100000_AddGroupAvatarKey', '8.0.0')";
            histCmd.ExecuteNonQuery();
        }
        using var check = conn.CreateCommand();
        check.CommandText =
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Chats' AND COLUMN_NAME = 'AvatarKey'";
        var exists = Convert.ToInt64(check.ExecuteScalar()) > 0;
        if (!exists)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE Chats ADD COLUMN AvatarKey varchar(500) NULL";
            alter.ExecuteNonQuery();
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("[Migration] AddGroupAvatarKey skipped: {Error}", ex.Message);
    }

    // AddMessageReactions migration
    try
    {
        using (var histCmd = conn.CreateCommand())
        {
            histCmd.CommandText =
                "INSERT IGNORE INTO `__EFMigrationsHistory` (MigrationId, ProductVersion) " +
                "VALUES ('20260325000000_AddMessageReactions', '8.0.0')";
            histCmd.ExecuteNonQuery();
        }
        using var createCmd = conn.CreateCommand();
        createCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS MessageReactions (
                Id char(36) NOT NULL,
                MessageId char(36) NOT NULL,
                UserId char(36) NOT NULL,
                Emoji varchar(10) NOT NULL,
                CreatedAt datetime NOT NULL,
                PRIMARY KEY (Id),
                UNIQUE KEY uq_reaction (MessageId, UserId, Emoji),
                CONSTRAINT fk_reaction_message FOREIGN KEY (MessageId) REFERENCES Messages(Id) ON DELETE CASCADE,
                CONSTRAINT fk_reaction_user FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
            )";
        createCmd.ExecuteNonQuery();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("[Migration] AddMessageReactions skipped: {Error}", ex.Message);
    }

    conn.Close();

    // Seed Master user if none exists
    var masterSection = app.Configuration.GetSection("MasterUser");
    var masterUsername = masterSection["Username"] ?? "master";
    var masterEmail    = masterSection["Email"]    ?? "master@imptrack.co.za";
    var masterPassword = masterSection["Password"] ?? "ImpTrack@Master2025";

    var masterExists = db.Users.Any(u => u.Role == UserRole.Master);
    if (!masterExists)
    {
        db.Users.Add(new User
        {
            Id           = Guid.NewGuid(),
            Username     = masterUsername,
            Email        = masterEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(masterPassword),
            Role         = UserRole.Master,
            MustChangePassword = false
        });
        db.SaveChanges();
    }
}

app.UseForwardedHeaders();

app.UseRouting();
app.UseCors(FrontendPolicy);

app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthentication();
app.UseAuthorization();

app.MapMethods("/api/{*path}", new[] { "OPTIONS" }, () => Results.Ok())
   .AllowAnonymous()
   .RequireCors(FrontendPolicy);

app.MapMethods("/hubs/{*path}", new[] { "OPTIONS" }, () => Results.Ok())
   .AllowAnonymous()
   .RequireCors(FrontendPolicy);

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    timestamp = DateTime.UtcNow,
    version = "1.0.0",
    environment = app.Environment.EnvironmentName
})).AllowAnonymous();

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

app.Run();
