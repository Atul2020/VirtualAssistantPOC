using HackathonBotPOC.Models;
using HackathonBotPOC.Services;
using Microsoft.AspNetCore.Mvc;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace HackathonBotPOC.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CommandController : ControllerBase
    {
        private readonly CommandService _commandService;

        public CommandController(CommandService commandService)
        {
            _commandService = commandService;
        }

        [HttpPost]
        public async Task<IActionResult> ProcessCommand([FromBody] CommandRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.Command))
            {
                return BadRequest("Command is required");
            }

            var (success, message) = await _commandService.ProcessCommandAsync(request.Command);
            if (success)
            {
                return Ok(message);
            }
            return StatusCode(500, message);
        }
    }
}
