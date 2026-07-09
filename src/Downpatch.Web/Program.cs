using Downpatch.Web.Components;
using Downpatch.Web.Endpoints;
using Downpatch.Web.Services;
using Markdig;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;

namespace Downpatch.Web
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services
                .AddRazorComponents()
                .AddInteractiveServerComponents();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddRouting();
            builder.Services.AddMemoryCache(o =>
            {
                o.SizeLimit = 512 * 1024 * 1024; 
            });

            builder.Services.AddSingleton(sp =>
            {
                var env = sp.GetRequiredService<IHostEnvironment>();
                var root = Path.GetFullPath(Path.Combine(env.ContentRootPath, "content"));
                var index = new ContentIndex(root);
                index.Build();
                return index;
            });
            builder.Services.AddSingleton(sp =>
            {
                var pipeline = new MarkdownPipelineBuilder()

                    // GitHub-style heading IDs
                    .UseAutoIdentifiers(Markdig.Extensions.AutoIdentifiers.AutoIdentifierOptions.GitHub)

                    // Enable nearly all standard Markdown + GFM features
                    .UseAdvancedExtensions()

                    // Better table support
                    .UsePipeTables()
                    .UseGridTables()

                    // GitHub task lists
                    .UseTaskLists()

                    // ~~strikethrough~~, subscript, superscript
                    .UseEmphasisExtras()

                    // {#custom-id .class}
                    .UseGenericAttributes()

                    .Build();

                return pipeline;
            });
            builder.Services.AddSingleton(sp =>
            {
                var env = sp.GetRequiredService<IHostEnvironment>();
                var root = Path.GetFullPath(Path.Combine(env.ContentRootPath, "content"));
                var nav = new Downpatch.Web.Services.NavStore(root);
                nav.Build();
                return nav;
            });

            builder.Services.AddSingleton<MarkdownPageService>();

            var app = builder.Build();


            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
            });

            var isDev = app.Environment.IsDevelopment();

            if (app.Environment.IsDevelopment())
            {
                app.Use(async (ctx, next) =>
                {
                    ctx.Response.Headers["Content-Security-Policy"] =
                        "default-src 'self'; " +
                        "base-uri 'self'; form-action 'self'; frame-ancestors 'none'; object-src 'none'; " +
                        "script-src 'self' 'unsafe-inline'; " +
                        "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; " +
                        "img-src 'self' data:; font-src 'self' https://cdn.jsdelivr.net data:; " +
                        "connect-src 'self' ws: wss: http://localhost:* https://localhost:*";
                    await next();
                });
            }
            else
            {
                app.Use(async (ctx, next) =>
                {
                    ctx.Response.Headers["Content-Security-Policy"] =
                        "default-src 'self'; " +
                        "base-uri 'self'; form-action 'self'; frame-ancestors 'none'; object-src 'none'; " +
                        "script-src 'self'; " +
                        "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; " +
                        "img-src 'self' data:; font-src 'self' https://cdn.jsdelivr.net data:; " +
                        "connect-src 'self'";
                    await next();
                });
            }

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(Path.Combine(app.Environment.ContentRootPath, "content")),
                RequestPath = "/content"
            });

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            app.UseHttpsRedirection();

            app.UseAntiforgery();

            app.MapStaticAssets();
            app.UseStaticFiles();
            app.MapRobots();
            app.MapSitemap();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();
            using var scope = app.Services.CreateScope();

            var pages = scope.ServiceProvider.GetRequiredService<Downpatch.Web.Services.MarkdownPageService>();
            pages.WarmAll();
            app.Run();
        }
    }
}
