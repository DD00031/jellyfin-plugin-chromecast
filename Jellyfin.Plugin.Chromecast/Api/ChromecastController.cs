using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Chromecast.Session;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Chromecast.Api;

/// <summary>
/// Admin-only endpoints behind the plugin's settings page: which Chromecasts were found, and a
/// way to check that one responds.
/// </summary>
[ApiController]
[Route("Chromecast")]
[Authorize(Policy = Policies.RequiresElevation)]
public class ChromecastController : ControllerBase
{
    private readonly ISessionManager _sessionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChromecastController"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    public ChromecastController(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// Lists the Chromecasts the plugin has found since the server started.
    /// </summary>
    /// <returns>The discovered Chromecasts.</returns>
    [HttpGet("Devices")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<ChromecastDeviceInfo>> GetDevices()
    {
        return Ok(GetControllers()
            .Select(c => new ChromecastDeviceInfo
            {
                DeviceId = c.DeviceId,
                Name = c.ReceiverName,
                DisplayName = c.DisplayName,
                Model = c.Model,
                Address = c.Host,
                IsOnline = c.IsOnline,
                IsHidden = c.IsHidden,
                IsReceiverOpen = c.IsReceiverOpen,
                NowPlaying = c.NowPlaying
            })
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Shows Jellyfin's ready screen on a Chromecast for a few seconds.
    /// </summary>
    /// <param name="deviceId">The Chromecast's device id, from <see cref="GetDevices"/>.</param>
    /// <returns>204 if the Chromecast responded, 404 if it is unknown, 503 if it did not respond.</returns>
    [HttpPost("Devices/{deviceId}/Test")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> TestDevice([FromRoute] string deviceId)
    {
        var controller = GetControllers().FirstOrDefault(c => string.Equals(c.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (controller is null)
        {
            return NotFound();
        }

        return await controller.TestAsync().ConfigureAwait(false)
            ? NoContent()
            : StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    private IEnumerable<ChromecastSessionController> GetControllers()
        => _sessionManager.Sessions.SelectMany(s => s.SessionControllers).OfType<ChromecastSessionController>();
}

/// <summary>
/// A discovered Chromecast, as shown in the settings page.
/// </summary>
public class ChromecastDeviceInfo
{
    /// <summary>Gets or sets the device id of the Chromecast's Jellyfin session.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the name the Chromecast itself reports.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the name shown in Jellyfin's cast menu.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the model name.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Gets or sets the network address.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether it answered a recent discovery scan.</summary>
    public bool IsOnline { get; set; }

    /// <summary>Gets or sets a value indicating whether it is hidden from the cast menu.</summary>
    public bool IsHidden { get; set; }

    /// <summary>Gets or sets a value indicating whether the Jellyfin receiver is open on it.</summary>
    public bool IsReceiverOpen { get; set; }

    /// <summary>Gets or sets the name of what is playing, if anything.</summary>
    public string? NowPlaying { get; set; }
}
