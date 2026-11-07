using Microsoft.AspNetCore.Mvc;

namespace AgentRag.Api.Controllers;

public sealed class PublicPagesController : Controller
{
    [HttpGet("/about")]
    public IActionResult About() => View();

    [HttpGet("/support")]
    public IActionResult Support() => View();

    [HttpGet("/privacy")]
    public IActionResult Privacy() => View();

    [HttpGet("/terms")]
    public IActionResult Terms() => View();
}
