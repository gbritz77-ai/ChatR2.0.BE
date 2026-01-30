// Program.cs
using System.Text;
using Chat.Api.Data;
using Chat.Api.Hubs;
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
                "https://d1gnxnjelgzuho.cloudfront.net" // ✅ add CloudFront too
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// ---------- Controllers & SignalR ----------
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddHttpContextAccessor();

// ---------- Forwarded headers (behind ALB/CloudFront) ----------
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // In AWS, proxies are not known at compile time:
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// ---------- JWT Auth ----------
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

// ✅ MUST be early so scheme/host are correct behind ALB/CloudFront
app.UseForwardedHeaders();

// If you terminate TLS at CloudFront/ALB, you typically DON'T need HTTPS redirection here.
// It can cause odd redirects in some setups. Safe to keep off for now.
//// app.UseHttpsRedirection();

app.UseCors(FrontendPolicy);

// ✅ Preflight handling: restrict to API + hubs only (NOT all paths)
// This avoids breaking /swagger/* and any other static endpoints.
app.MapMethods("/api/{*path}", new[] { "OPTIONS" }, () => Results.Ok())
   .AllowAnonymous()
   .RequireCors(FrontendPolicy);

app.MapMethods("/hubs/{*path}", new[] { "OPTIONS" }, () => Results.Ok())
   .AllowAnonymous()
   .RequireCors(FrontendPolicy);

// ✅ Enable Swagger when you want it
// Option A: enable always (useful while debugging prod):
app.UseSwagger();
app.UseSwaggerUI();

// Option B (alternative): only enable when env var is set
// if (app.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("EnableSwagger"))
// {
//     app.UseSwagger();
//     app.UseSwaggerUI();
// }

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
