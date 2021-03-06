using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetCoreKit.Domain;
using NetCoreKit.Infrastructure.AspNetCore.CleanArch;
using NetCoreKit.Infrastructure.AspNetCore.Configuration;
using NetCoreKit.Infrastructure.AspNetCore.Miniservice.ExternalSystems;
using NetCoreKit.Infrastructure.AspNetCore.OpenApi;
using NetCoreKit.Infrastructure.AspNetCore.Rest;
using NetCoreKit.Infrastructure.AspNetCore.Validation;
using NetCoreKit.Infrastructure.Features;
using Newtonsoft.Json.Serialization;
using Swashbuckle.AspNetCore.Swagger;

namespace NetCoreKit.Infrastructure.AspNetCore.Miniservice
{
  public static partial class ServiceCollectionExtensions
  {
    public static IServiceCollection AddMiniService(
      this IServiceCollection services,
      Action<IServiceCollection> preScopeAction = null,
      Action<IServiceCollection, IServiceProvider> afterDbScopeAction = null)
    {
      services.AddFeatureToggle();

      using (var scope = services.BuildServiceProvider().GetService<IServiceScopeFactory>().CreateScope())
      {
        var svcProvider = scope.ServiceProvider;
        var config = svcProvider.GetRequiredService<IConfiguration>();
        var env = svcProvider.GetRequiredService<IHostingEnvironment>();
        var feature = svcProvider.GetRequiredService<IFeature>();

        // let registering the database providers or others from the outside
        preScopeAction?.Invoke(services);
        // services.AddScoped<DbHealthCheckAndMigration>();

        // let outside inject more logic (like more healthcheck endpoints...)
        afterDbScopeAction?.Invoke(services, svcProvider);

        // RestClient out of the box
        services.AddRestClientCore();

        // DomainEventBus for handling the published event from the domain object
        services.AddSingleton<IDomainEventBus, MemoryDomainEventBus>();

        // #3
        if (feature.IsEnabled("CleanArch"))
          services.AddCleanArch(config.LoadFullAssemblies());

        services.AddCacheCore();

        // #4
        if (feature.IsEnabled("ApiVersion"))
          services.AddApiVersionCore(config);

        // #5
        services.AddMvcCore(config);

        // #6
        services.AddDetailExceptionCore();

        // #7
        if (feature.IsEnabled("AuthN"))
          services.AddAuthNCore(config, env);

        // #8
        if (feature.IsEnabled("OpenApi"))
          services.AddOpenApiCore(config, feature);

        // #9
        services.AddCorsCore();

        // #10
        services.AddHeaderForwardCore(env);

        if (feature.IsEnabled("OpenApi:Profiler"))
          services.AddApiProfilerCore();
      }

      return services;
    }

    public static IServiceCollection AddRestClientCore(this IServiceCollection services)
    {
      services.AddHttpContextAccessor();
      services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
      services.AddSingleton<IActionContextAccessor, ActionContextAccessor>();
      services.AddSingleton<IUrlHelper>(fac => new UrlHelper(fac.GetService<IActionContextAccessor>().ActionContext));
      services.AddHttpPolly<RestClient>();

      return services;
    }

    public static IServiceCollection AddCacheCore(this IServiceCollection services)
    {
      services.AddMemoryCache();
      services.AddResponseCaching();

      return services;
    }

    public static IServiceCollection AddApiVersionCore(this IServiceCollection services, IConfiguration config)
    {
      services.AddRouting(o => o.LowercaseUrls = true);
      services
        .AddMvcCore()
        .AddVersionedApiExplorer(
          o =>
          {
            o.GroupNameFormat = "'v'VVV";
            o.SubstituteApiVersionInUrl = true;
          })
        .AddJsonFormatters(o => o.ContractResolver = new CamelCasePropertyNamesContractResolver())
        .AddDataAnnotations();

      services.AddApiVersioning(o =>
      {
        o.ReportApiVersions = true;
        o.AssumeDefaultVersionWhenUnspecified = true;
        o.DefaultApiVersion = ParseApiVersion(config.GetValue<string>("API_VERSION"));
      });

      return services;
    }

    public static IServiceCollection AddMvcCore(this IServiceCollection services, IConfiguration config)
    {
      var mvcBuilder = services.AddMvc();
      if (config.LoadFullAssemblies() != null && config.LoadFullAssemblies().Any())
        foreach (var assembly in config.LoadFullAssemblies())
          mvcBuilder = mvcBuilder.AddApplicationPart(assembly);
      mvcBuilder.SetCompatibilityVersion(CompatibilityVersion.Version_2_1);

      return services;
    }

    public static IServiceCollection AddDetailExceptionCore(this IServiceCollection services)
    {
      services.Configure<ApiBehaviorOptions>(options =>
      {
        options.InvalidModelStateResponseFactory = ctx => new ValidationProblemDetailsResult();
      });

      return services;
    }

    public static IServiceCollection AddAuthNCore(this IServiceCollection services, IConfiguration config, IHostingEnvironment env)
    {
      JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

      services
        .AddAuthentication(options =>
        {
          options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
          options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
          options.Authority = GetAuthUri(config, env);
          options.RequireHttpsMetadata = false;
          options.Audience = config.GetAudience();
        });

      services.AddAuthorization(c =>
      {
        foreach (var claimToScope in config.GetClaims())
          c.AddPolicy(claimToScope.Key, p => p.RequireClaim("scope", claimToScope.Value));
      });

      return services;
    }

    public static IServiceCollection AddOpenApiCore(this IServiceCollection services, IConfiguration config, IFeature feature)
    {
      if (config.GetSection("Features:OpenApi") == null)
        throw new CoreException("Please add OpenApi configuration or disabled OpenAPI.");

      services.Configure<OpenApiOptions>(config.GetSection("Features:OpenApi"));

      services.AddSwaggerGen(c =>
      {
        var provider = services.BuildServiceProvider().GetRequiredService<IApiVersionDescriptionProvider>();

        c.DescribeAllEnumsAsStrings();

        foreach (var description in provider.ApiVersionDescriptions)
          c.SwaggerDoc(
            description.GroupName,
            CreateInfoForApiVersion(config, description));

        // c.IncludeXmlComments (XmlCommentsFilePath);

        if (feature.IsEnabled("AuthN"))
          c.AddSecurityDefinition("oauth2", new OAuth2Scheme
          {
            Type = "oauth2",
            Flow = "implicit",
            AuthorizationUrl = $"{GetExternalAuthUri(config)}/connect/authorize",
            TokenUrl = $"{GetExternalAuthUri(config)}/connect/token",
            Scopes = config.GetScopes()
          });

        c.EnableAnnotations();

        if (feature.IsEnabled("AuthN"))
          c.OperationFilter<SecurityRequirementsOperationFilter>();

        c.OperationFilter<DefaultValuesOperationFilter>();
        c.SchemaFilter<SwaggerExcludeSchemaFilter>();
        c.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());
      });

      return services;
    }

    public static IServiceCollection AddCorsCore(this IServiceCollection services)
    {
      services.AddCors(options =>
      {
        options.AddPolicy("CorsPolicy",
          policy => policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials());
      });

      return services;
    }

    public static IServiceCollection AddHeaderForwardCore(this IServiceCollection services, IHostingEnvironment env)
    {
      if (!env.IsDevelopment())
        services.Configure<ForwardedHeadersOptions>(options =>
        {
          options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        });

      return services;
    }

    public static IServiceCollection AddApiProfilerCore(this IServiceCollection services)
    {
      services.AddMiniProfiler(options =>
        options.RouteBasePath = "/profiler"
      );
      
      return services;
    }

    private static IServiceCollection AddExternalSystemHealthChecks(this IServiceCollection services,
      Func<IServiceProvider, IEnumerable<IExternalSystem>> extendExternalSystem = null)
    {
      return services.AddScoped(p =>
      {
        var results = extendExternalSystem?.Invoke(p);
        return results == null
          ? new List<IExternalSystem> {p.GetService<DbHealthCheckAndMigration>()}
          : results.Append(p.GetService<DbHealthCheckAndMigration>());
      });
    }

    private static ApiVersion ParseApiVersion(string serviceVersion)
    {
      if (string.IsNullOrEmpty(serviceVersion))
      {
        throw new CoreException("[CS] ServiceVersion is null or empty.");
      }

      const string pattern = @"(.)|(-)";
      var results = Regex.Split(serviceVersion, pattern)
        .Where(x => x != string.Empty && x != "." && x != "-")
        .ToArray();

      if (results == null || results.Count() < 2)
      {
        throw new CoreException("[CS] Could not parse ServiceVersion.");
      }

      if (results.Count() > 2)
      {
        return new ApiVersion(
          Convert.ToInt32(results[0]),
          Convert.ToInt32(results[1]),
          results[2]);
      }

      return new ApiVersion(
        Convert.ToInt32(results[0]),
        Convert.ToInt32(results[1]));
    }

    private static string GetAuthUri(IConfiguration config, IHostingEnvironment env)
    {
      return config.GetHostUri(env, "Auth");
    }

    private static string GetExternalAuthUri(IConfiguration config)
    {
      return config.GetExternalHostUri("Auth");
    }

    private static Info CreateInfoForApiVersion(IConfiguration config, ApiVersionDescription description)
    {
      var info = new Info()
      {
        Title = $"{config.GetValue("Features:OpenApi:ApiInfo:Title", "API")} {description.ApiVersion}",
        Version = description.ApiVersion.ToString(),
        Description = config.GetValue("Features:OpenApi:ApiInfo:Description", "An application with Swagger, Swashbuckle, and API versioning."),
        Contact = new Contact()
        {
          Name = config.GetValue("Features:OpenApi:ApiInfo:ContactName", "Vietnam Devs"),
          Email = config.GetValue("Features:OpenApi:ApiInfo:ContactEmail", "vietnam.devs.group@gmail.com")
        },
        TermsOfService = config.GetValue("Features:OpenApi:ApiInfo:TermOfService", "Shareware"),
        License = new License()
        {
          Name = config.GetValue("Features:OpenApi:ApiInfo:LicenseName", "MIT"),
          Url = config.GetValue("Features:OpenApi:ApiInfo:LicenseUrl", "https://opensource.org/licenses/MIT")
        }
      };

      if (description.IsDeprecated)
      {
        info.Description += " This API version has been deprecated.";
      }

      return info;
    }
  }
}
