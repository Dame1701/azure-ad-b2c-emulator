// SPDX-License-Identifier: MIT
// Part of azure-ad-b2c-emulator: a local stand-in for Azure AD B2C.

using System.Text;
using System.Text.Encodings.Web;
using AzureAdB2cEmulator.Configuration;
using AzureAdB2cEmulator.Models;

namespace AzureAdB2cEmulator.Services;

/// <summary>
///     Renders the login / password-reset screens. The outer page is loaded from
///     <c>wwwroot/templates/login-layout.html</c> at runtime, so the whole page chrome can
///     be replaced by mounting your own template file over it - no rebuild required. The C#
///     only renders the form fields and developer quick-pick into the {{Body}} placeholder.
/// </summary>
public sealed class LoginPageRenderer
{
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    private readonly EmulatorOptions _options;
    private readonly string _templatePath;
    private string? _cachedTemplate;

    public LoginPageRenderer(EmulatorOptions options, IWebHostEnvironment environment)
    {
        _options = options;
        var root = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
        _templatePath = Path.Combine(root, "templates", "login-layout.html");
    }

    public string RenderSignIn(AuthorizeRequest request, string? error, bool isPasswordReset = false)
    {
        var title = isPasswordReset ? "Reset Password" : "Sign-in";
        var heading = isPasswordReset ? "Reset Password" : "Sign in";

        var body = new StringBuilder();
        body.Append($"<div class=\"card-title\">{Encoder.Encode(title)}</div>");
        body.Append("<div id=\"api\">");

        if (!string.IsNullOrEmpty(error))
        {
            body.Append($"<div class=\"error pageLevel\">{Encoder.Encode(error)}</div>");
        }

        body.Append("<form method=\"post\" autocomplete=\"off\">");
        body.Append(HiddenFields(request));

        body.Append("<div class=\"field\">");
        body.Append("<label for=\"email\">Email address</label>");
        body.Append("<input id=\"email\" name=\"email\" type=\"email\" placeholder=\"name@example.com\" autofocus />");
        body.Append("</div>");

        if (!isPasswordReset)
        {
            body.Append("<div class=\"field\">");
            body.Append("<label for=\"password\">Password</label>");
            body.Append("<input id=\"password\" name=\"password\" type=\"password\" placeholder=\"Any value (not checked)\" />");
            body.Append("</div>");
        }

        body.Append($"<div class=\"buttons\"><button type=\"submit\">{Encoder.Encode(heading)}</button></div>");
        body.Append("</form>");

        body.Append(RenderQuickPick(request));
        body.Append("</div>");

        return Layout(title, body.ToString());
    }

    public string RenderInfo(string title, string message)
    {
        var body = $"<div class=\"card-title\">{Encoder.Encode(title)}</div>" +
                   $"<div id=\"api\"><p>{Encoder.Encode(message)}</p></div>";
        return Layout(title, body);
    }

    private string RenderQuickPick(AuthorizeRequest request)
    {
        if (_options.Users.Count == 0)
        {
            return string.Empty;
        }

        var html = new StringBuilder();
        html.Append("<div class=\"quick-pick\">");
        html.Append("<div class=\"quick-pick-title\">Developer quick sign-in</div>");
        html.Append("<form method=\"post\">");
        html.Append(HiddenFields(request));

        html.Append("<div class=\"field\">");
        html.Append("<select name=\"email\" class=\"user-select\">");
        foreach (var group in _options.Users.GroupBy(u => u.Group ?? "Users"))
        {
            html.Append($"<optgroup label=\"{Encoder.Encode(group.Key)}\">");
            foreach (var user in group)
            {
                var badge = string.IsNullOrWhiteSpace(user.Label) ? string.Empty : $" ({user.Label})";
                html.Append($"<option value=\"{Encoder.Encode(user.Email)}\">" +
                            $"{Encoder.Encode(user.DisplayName)}{Encoder.Encode(badge)}</option>");
            }

            html.Append("</optgroup>");
        }

        html.Append("</select>");
        html.Append("</div>");

        html.Append("<div class=\"buttons\"><button type=\"submit\">Sign in as selected user</button></div>");
        html.Append("</form>");
        html.Append("</div>");
        return html.ToString();
    }

    private static string HiddenFields(AuthorizeRequest request)
    {
        var fields = new (string Name, string? Value)[]
        {
            ("client_id", request.ClientId),
            ("redirect_uri", request.RedirectUri),
            ("response_type", request.ResponseType),
            ("response_mode", request.ResponseMode),
            ("scope", request.ScopeString),
            ("state", request.State),
            ("nonce", request.Nonce),
            ("code_challenge", request.CodeChallenge),
            ("code_challenge_method", request.CodeChallengeMethod)
        };

        var html = new StringBuilder();
        foreach (var (name, value) in fields)
        {
            if (value is not null)
            {
                html.Append($"<input type=\"hidden\" name=\"{name}\" value=\"{Encoder.Encode(value)}\" />");
            }
        }

        return html.ToString();
    }

    private string Layout(string title, string cardBody)
    {
        var brandName = _options.Branding.ProductName;
        var brand = string.IsNullOrWhiteSpace(_options.Branding.LogoPath)
            ? $"<div class=\"brand\">{Encoder.Encode(brandName)}</div>"
            : $"<div class=\"brand brand-logo\" style=\"background-image:url('{Encoder.Encode(_options.Branding.LogoPath!)}')\">{Encoder.Encode(brandName)}</div>";

        return Template()
            .Replace("{{Title}}", Encoder.Encode($"{brandName} - {title}"))
            .Replace("{{StylesHref}}", "/assets/styles.css")
            .Replace("{{Brand}}", brand)
            .Replace("{{Tag}}", Encoder.Encode(_options.Branding.EmulatorTag))
            .Replace("{{Body}}", cardBody);
    }

    private string Template()
    {
        // Read once and cache. Mounting a replacement file happens at container start, so a
        // single read is enough; falls back to a built-in layout if the file is missing.
        return _cachedTemplate ??= File.Exists(_templatePath)
            ? File.ReadAllText(_templatePath)
            : FallbackTemplate;
    }

    private const string FallbackTemplate = """
                                            <!DOCTYPE html>
                                            <html lang="en">
                                            <head>
                                              <meta charset="UTF-8">
                                              <meta name="viewport" content="width=device-width, initial-scale=1.0">
                                              <title>{{Title}}</title>
                                              <link href="{{StylesHref}}" rel="stylesheet">
                                            </head>
                                            <body>
                                              <div class="container">
                                                <div class="main-toolbar">{{Brand}}<div class="emulator-tag">{{Tag}}</div></div>
                                                <div class="content"><div class="card">{{Body}}</div></div>
                                              </div>
                                            </body>
                                            </html>
                                            """;
}
