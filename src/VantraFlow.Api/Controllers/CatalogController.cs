using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// What Flow can listen to and do, described: every trigger and action (its app, key, title, required config, what it
/// does and hands on, and whether it's a paid-plan one), and the steps that aren't an app's action.
/// <code>
/// GET api/catalog → { triggers, actions, steps }
/// </code>
/// </summary>
[ApiController]
[Authorize]
public sealed class CatalogController : ControllerBase
{
    [HttpGet("api/catalog")]
    public IActionResult Get()
    {
        static object Entry(CatalogEntry e) => new
        {
            app = e.App, key = e.Key, title = e.Title, description = e.Description, premium = e.Premium,
            required = e.Required.Select(r => new { key = r, label = e.Label(r) }),
        };
        return Ok(new
        {
            triggers = FlowCatalog.Triggers.Select(Entry),
            actions = FlowCatalog.Actions.Select(Entry),
            steps = FlowCatalog.Steps.Select(s => new { type = s.Type, title = s.Title, description = s.Description, premium = s.Premium }),
        });
    }
}
