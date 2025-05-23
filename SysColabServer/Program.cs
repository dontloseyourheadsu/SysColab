﻿using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using SysColab.Shared;

var builder = WebApplication.CreateSlimBuilder(args);

// Habilitar la configuración de HTTPS en Kestrel
builder.WebHost.UseKestrelHttpsConfiguration();

// 5 MB hard cap for any request body (particularly file uploads)
const long FiveMb = 5L * 1024 * 1024;

// Listen on HTTP :5268  and HTTPS :7268 as before.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5268);                                        // HTTP
    options.ListenAnyIP(7268, o => o.UseHttps());                      // HTTPS
    options.Limits.MaxRequestBodySize = FiveMb;                        // 🔒 5 MB
});

// Add logging services
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Debug);

// Add CORS services
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Enable CORS
app.UseCors("AllowAll");

// Enable WebSocket support
app.UseWebSockets();

// Get logger from the app's service provider
var logger = app.Services.GetRequiredService<ILogger<Program>>();

var connectedDevices = new ConcurrentDictionary<Guid, (DeviceInfo DeviceInfo, WebSocket WebSocket)>();

// Temporary store for mapping pending connections by token/uuid
var pendingRegistrations = new ConcurrentDictionary<Guid, (DeviceInfo DeviceInfo, TaskCompletionSource<WebSocket> Tcs)>();

// Transient blob store for uploaded files (≤ 5 MB each)
var files = new ConcurrentDictionary<Guid, (string Name, string ContentType, byte[] Bytes)>();


/// <summary>
/// Notifies all connected devices about a device status change.
/// </summary>
/// <param name="deviceInfo">The device information to include in the notification.</param>
/// <param name="messageType">The type of message to send (e.g., "device_connected", "device_disconnected").</param>
async Task NotifyDeviceStatusChangeAsync(DeviceInfo deviceInfo, string messageType)
{
    logger.LogDebug("{MessageType} notification for device: {DeviceId}, Name: {DeviceName}",
        messageType, deviceInfo.Id, deviceInfo.Name);

    // Create a message to broadcast to all connected devices
    var notificationMessage = new RelayMessage
    {
        TargetId = "all", // Special value to indicate broadcasting
        MessageType = messageType,
        SerializedJson = JsonSerializer.Serialize(deviceInfo)
    };

    // Serialize the message to JSON
    var notificationJson = JsonSerializer.Serialize(notificationMessage);
    var notificationBytes = Encoding.UTF8.GetBytes(notificationJson);

    // Broadcast to all connected devices except the one that triggered the event
    foreach (var device in connectedDevices)
    {
        // Skip sending to the device itself that triggered the event
        if (device.Key == deviceInfo.Id)
        {
            continue;
        }

        try
        {
            // Check if the WebSocket is open before sending
            if (device.Value.WebSocket.State == WebSocketState.Open)
            {
                // Send the notification message to the device
                await device.Value.WebSocket.SendAsync(
                    new ArraySegment<byte>(notificationBytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);

                logger.LogDebug("Sent {MessageType} notification to device: {DeviceId}",
                    messageType, device.Key);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending {MessageType} notification to device: {DeviceId}",
                messageType, device.Key);
        }
    }
}

// WebSocket endpoint that waits for registration token (UUID)
app.Map("/ws", async context =>
{
    logger.LogDebug("WebSocket connection attempt received");

    // Check if the request is a WebSocket request
    if (!context.WebSockets.IsWebSocketRequest)
    {
        logger.LogWarning("Non-WebSocket request received at WebSocket endpoint");
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // Accept the WebSocket connection
    logger.LogDebug("Accepting WebSocket connection for a device");
    var webSocket = await context.WebSockets.AcceptWebSocketAsync();

    // Check if the UUID is provided in the query string
    var uuidString = context.Request.Query["uuid"].ToString();
    logger.LogDebug("WebSocket connection with UUID: {UUID}", uuidString);

    // Validate the UUID
    var uuid = Guid.TryParse(uuidString, out var parsedUuid) ? parsedUuid : Guid.Empty;

    // Check if the UUID is valid and if the registration is pending
    if (!pendingRegistrations.TryRemove(uuid, out var pendingRegistration))
    {
        // If the UUID is not valid or not found, return an error
        logger.LogWarning("Invalid or unregistered UUID in WebSocket connection: {UUID}", uuidString);

        // Send an error message to the WebSocket
        if (webSocket.State == WebSocketState.Open)
        {
            // Send an error message to the WebSocket
            var errorBytes = Encoding.UTF8.GetBytes("Invalid or unregistered UUID. Must register first.");
            await webSocket.SendAsync(
                new ArraySegment<byte>(errorBytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);

            // Close the WebSocket connection with a policy violation status
            await webSocket.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                "Invalid registration",
                CancellationToken.None);
        }
        return;
    }

    // If the UUID is valid and registration is pending, complete the registration
    var tcs = pendingRegistration.Tcs;
    tcs.SetResult(webSocket);

    // Register the WebSocket with the UUID
    connectedDevices[uuid] = (pendingRegistration.DeviceInfo, webSocket);

    logger.LogInformation("Device registered and connected: {DeviceId}, Name: {DeviceName}",
        uuid, pendingRegistration.DeviceInfo.Name);

    // Notify all connected devices about the new device connection
    await NotifyDeviceStatusChangeAsync(pendingRegistration.DeviceInfo, "device_connected");

    // Start listening for messages from the WebSocket
    var buffer = new byte[1024 * 4];
    var messageBuilder = new StringBuilder();

    try
    {
        // Loop to receive messages from the WebSocket
        while (webSocket.State == WebSocketState.Open)
        {
            // Reset the StringBuilder for a new message
            messageBuilder.Clear();
            WebSocketReceiveResult result;

            // Read potentially fragmented message
            do
            {
                // Receive the message from the WebSocket
                result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    CancellationToken.None);

                // Check if the message is a text message
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    // Append the received message fragment to the StringBuilder
                    var messageFragment = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    messageBuilder.Append(messageFragment);
                }
            }
            while (!result.EndOfMessage);

            // Check if the WebSocket is closed
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (connectedDevices.TryRemove(uuid, out var removedDevice))
                {
                    // Notify all connected devices about the device disconnect
                    await NotifyDeviceStatusChangeAsync(removedDevice.DeviceInfo, "device_disconnected");
                }

                // Close the WebSocket connection gracefully
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Closing",
                    CancellationToken.None);
                logger.LogInformation("Device disconnected: {DeviceId}", uuid);
                break;
            }

            // Handle incoming messages
            var receivedMessage = messageBuilder.ToString();
            logger.LogDebug("Received message from {DeviceId}: {MessageLength} bytes", uuid, receivedMessage.Length);

            try
            {
                // Deserialize the message to RelayMessage
                var message = JsonSerializer.Deserialize<RelayMessage>(receivedMessage);

                // Check if the message is valid
                if (message == null || string.IsNullOrWhiteSpace(message.TargetId) ||
                    string.IsNullOrWhiteSpace(message.SerializedJson) ||
                    string.IsNullOrWhiteSpace(message.MessageType))
                {
                    logger.LogWarning("Invalid message format received from {DeviceId}", uuid);
                    throw new Exception("Invalid message format. Must include targetId, serializedJson, and messageType.");
                }

                // Parse the targetId from the message
                var targetId = Guid.Parse(message.TargetId);
                logger.LogDebug("Message relay request from {SourceId} to {TargetId} of type {MessageType}",
                    uuid, targetId, message.MessageType);

                // Relay the message to the target device
                if (connectedDevices.TryGetValue(targetId, out var targetDevice) &&
                    targetDevice.WebSocket.State == WebSocketState.Open)
                {
                    // Send the message to the target device 
                    // - keeping the original structure with messageType
                    logger.LogDebug("Relaying message from {SourceId} to {TargetId} of type {MessageType}",
                        uuid, targetId, message.MessageType);

                    // We'll send the original message as-is to preserve the messageType
                    await targetDevice.WebSocket.SendAsync(
                        new ArraySegment<byte>(Encoding.UTF8.GetBytes(receivedMessage)),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);

                    logger.LogDebug("Message successfully relayed to {TargetId}", targetId);
                }
                else // if the target device is not found or offline
                {
                    // Create an error message with error messageType
                    var errorResponse = new RelayMessage
                    {
                        TargetId = uuid.ToString(), // Send back to the sender
                        MessageType = "error",
                        SerializedJson = JsonSerializer.Serialize(new
                        {
                            errorCode = "DEVICE_OFFLINE",
                            message = $"Target '{message.TargetId}' not found or offline."
                        })
                    };

                    // Send an error message back to the sender
                    logger.LogWarning("Target device {TargetId} not found or offline", targetId);
                    var errorMsg = JsonSerializer.Serialize(errorResponse);
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(Encoding.UTF8.GetBytes(errorMsg)),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                // Handle deserialization errors or other exceptions
                logger.LogError(ex, "Error processing message from {DeviceId}", uuid);

                // Create an error message
                var errorResponse = new RelayMessage
                {
                    TargetId = uuid.ToString(), // Send back to the sender
                    MessageType = "error",
                    SerializedJson = JsonSerializer.Serialize(new
                    {
                        errorCode = "PROCESSING_ERROR",
                        message = ex.Message
                    })
                };

                // Send an error message back to the sender
                var errorMsg = JsonSerializer.Serialize(errorResponse);
                await webSocket.SendAsync(
                    new ArraySegment<byte>(Encoding.UTF8.GetBytes(errorMsg)),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unexpected error in WebSocket connection for {DeviceId}", uuid);

        // Get the device info before removing it
        if (connectedDevices.TryGetValue(uuid, out var deviceBeforeRemoval))
        {
            // Remove the device from connected devices if an exception occurs
            connectedDevices.TryRemove(uuid, out _);

            // Notify all connected devices about the device disconnect
            await NotifyDeviceStatusChangeAsync(deviceBeforeRemoval.DeviceInfo, "device_disconnected");
        }

        // Attempt to close the WebSocket gracefully if it's still open
        if (webSocket.State == WebSocketState.Open)
        {
            await webSocket.CloseAsync(
                WebSocketCloseStatus.InternalServerError,
                "An unexpected error occurred",
                CancellationToken.None);
        }
    }
});

// POST /register => accepts UUID and prepares to associate with WebSocket
app.MapPost("/api/register", (DeviceInfo deviceInfo) =>
{
    logger.LogDebug("Registration attempt for device: {DeviceId}, Name: {DeviceName}", deviceInfo.Id, deviceInfo.Name);

    // Validate the device info
    var tcs = new TaskCompletionSource<WebSocket>();
    if (!pendingRegistrations.TryAdd(deviceInfo.Id, (deviceInfo, tcs)))
    {
        logger.LogWarning("Failed to register device: {DeviceId}. UUID already pending or registered", deviceInfo.Id);
        return Results.BadRequest("UUID already pending or registered.");
    }

    // Create a new WebSocket connection
    logger.LogInformation("Device registered and pending WebSocket connection: {DeviceId}", deviceInfo.Id);
    return Results.Ok("Ready to connect WebSocket.");
});

// GET /api/connected-devices => returns list of connected devices
app.MapGet("/api/connected-devices", () =>
{
    // Check if there are any connected devices
    logger.LogDebug("Request received for connected devices list");
    var devices = connectedDevices.Values.Select(x => x.DeviceInfo).ToList();
    logger.LogDebug("Returning {Count} connected devices", devices.Count);

    // If no devices are connected, return a 404 Not Found
    if (devices is null or { Count: 0 })
    {
        return Results.NotFound("No connected devices found.");
    }

    // Return the list of connected devices
    return Results.Ok(devices);
});

// POST /api/file => uploads a file and notifies the target device
app.MapPost("/api/file", async (HttpRequest req) =>
{
    // Check if the request is a multipart/form-data request
    if (!req.HasFormContentType) return Results.BadRequest("multipart/form-data expected");
    var form = await req.ReadFormAsync();

    // Validate the form data
    if (!Guid.TryParse(form["targetId"], out var targetId)) return Results.BadRequest("targetId missing");
    if (!Guid.TryParse(form["senderId"], out var senderId)) return Results.BadRequest("senderId missing");

    // Validate the file upload
    var formFile = form.Files["file"];
    if (formFile is null || formFile.Length == 0) return Results.BadRequest("file field missing");
    if (formFile.Length > FiveMb) return Results.BadRequest("File exceeds 5 MB");

    // Validate the file name and content type
    await using var ms = new MemoryStream();
    await formFile.CopyToAsync(ms);
    var fileId = Guid.NewGuid();
    files[fileId] = (formFile.FileName, formFile.ContentType, ms.ToArray());

    // Push lightweight offer
    var offer = new RelayMessage
    {
        TargetId = targetId.ToString(),
        MessageType = "file_offer",
        SerializedJson = JsonSerializer.Serialize(new FileOffer(fileId, formFile.FileName, formFile.Length, senderId))
    };

    // Send the offer to the target device
    if (connectedDevices.TryGetValue(targetId, out var target) && target.WebSocket.State == WebSocketState.Open)
    {
        await target.WebSocket.SendAsync(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(offer)),
            WebSocketMessageType.Text, true, CancellationToken.None);
    }

    // If the target device is not connected, send an error message back to the sender
    return Results.Ok(new { fileId });
});

// GET /api/file/{id:guid} => downloads a file
app.MapGet("/api/file/{id:guid}", (Guid id) =>
{
    // Check if the file exists in the temporary store
    if (!files.TryRemove(id, out var blob)) return Results.NotFound();
    // Check if the file is still in the store
    return Results.File(blob.Bytes, blob.ContentType, blob.Name);
});

logger.LogInformation("WebSocket server starting up");
await app.RunAsync();
logger.LogInformation("WebSocket server shutting down");