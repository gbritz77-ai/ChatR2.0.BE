// Program.cs
using System.Text;
using Chat.Api.Data;
using Chat.Api.Hubs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// ---------- DbContext ----------
builder.Services.AddDbContext<ChatDbContext>(options =>
{
    var cs = builder.Configuration.GetConnectionString("DefaultConnection")
             ?? throw new InvalidOperationException("DefaultConnection is missing.");

    // ✅ Don't use AutoDetect in ECS (it opens a connection during startup)
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
                "https://main.d1imfsef8qotjc.amplifyapp.com"
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            // If you are NOT using cookies, you can remove AllowCredentials().
            // Leaving it enabled is OK as long as you use WithOrigins (not AllowAnyOrigin).
            .AllowCredentials();
    });
});

// ---------- Controllers & SignalR ----------
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddHttpContextAccessor();

// ---------- JWT Auth (won't crash if missing) ----------
var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection["Key"];
var jwtIssuer = jwtSection["Issuer"];
var jwtAudience = jwtSection["Audience"];

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        // Always allow SignalR token-from-query
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

        // If config is missing, don't crash the container; auth will fail with 401 when used.
        if (string.IsNullOrWhiteSpace(jwtKey) ||
            string.IsNullOrWhiteSpace(jwtIssuer) ||
            string.IsNullOrWhiteSpace(jwtAudience))
        {
            Console.WriteLine("⚠ JWT config missing (Jwt:Key/Issuer/Audience). API will start, but protected endpoints will return 401.");
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateIssuerSigningKey = false,
                ValidateLifetime = false
            };
            return;
        }

        var keyBytes = Encoding.UTF8.GetBytes(jwtKey);

        options.RequireHttpsMetadata = false; // OK behind ALB/CloudFront
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
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

// Swagger only in dev (fine)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// IMPORTANT behind ALB/CloudFront: trust X-Forwarded-* so HTTPS is detected correctly
app.UseForwardedHeaders();

app.UseHttpsRedirection();

app.UseCors(FrontendPolicy);

// ✅ FIX: Handle ALL CORS preflight (OPTIONS) BEFORE controllers
app.MapMethods("{*path}", new[] { "OPTIONS" }, () => Results.Ok())
   .AllowAnonymous()
   .RequireCors(FrontendPolicy);

app.UseAuthentication();
app.UseAuthorization();

// Health check endpoint for ECS/Load Balancer
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
