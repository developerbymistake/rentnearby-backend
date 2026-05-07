using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Api.Endpoints;
using RentNearBy.Api.Extensions;
using RentNearBy.Api.Mappings;
using RentNearBy.Api.Middleware;
using RentNearBy.Api.Validators;

var builder = WebApplication.CreateBuilder(args);

DtoMappings.ConfigureMappings();

builder.Services.AddApplicationServices(builder.Configuration);
builder.Services.AddJwtAuthentication(builder.Configuration);

builder.Services.AddValidatorsFromAssemblyContaining<SendOtpRequestValidator>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "RentNearBy API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "JWT Authorization header. Example: \"Bearer {token}\"",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RentNearBy.Infrastructure.Data.ApplicationDbContext>();
    var resetDb = app.Environment.IsDevelopment()
        || string.Equals(app.Configuration["RESET_DB"], "true", StringComparison.OrdinalIgnoreCase);

    if (resetDb)
    {
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
    }
    else
    {
        db.Database.EnsureCreated();
    }
    await RentNearBy.Infrastructure.Data.DataSeeder.SeedAsync(db);
}

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseCors("AllowAll");
app.UsePhotoStorage();
app.UseAuthentication();
app.UseAuthorization();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGroup("/api/v1/auth")
    .WithTags("Authentication")
    .MapAuthEndpoints();

app.MapGroup("/api/v1/users")
    .WithTags("Users")
    .MapUsersEndpoints();

app.MapGroup("/api/v1/listings")
    .WithTags("Listings")
    .MapListingsEndpoints();

app.MapGroup("/api/v1/admin")
    .WithTags("Admin")
    .MapAdminEndpoints();

app.Run();
