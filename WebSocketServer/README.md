# Juke Box WebSocket Server

Real-time WebSocket server for synchronizing tracklist updates between master and slave Unity applications.

## 🚀 Quick Start

### Prerequisites
- Node.js (v14 or higher)
- npm or yarn

### Installation

1. Navigate to the WebSocketServer directory:
   ```bash
   cd WebSocketServer
   ```

2. Install dependencies:
   ```bash
   npm install
   ```

3. Start the server:
   ```bash
   npm start
   ```

The server will start on `ws://localhost:3000` by default.

### Development Mode

For auto-restart during development:
```bash
npm run dev
```

## 📡 WebSocket API

### Connection
- **Endpoint**: `ws://localhost:3000`
- **Protocol**: WebSocket
- **Message Format**: JSON

### TracklistUpdate Message Format

```json
{
  "operationType": "pause|resume|skip|insert",
  "songTitle": "Song Name",
  "status": "paused|playing|skipped|queued",
  "currentTime": 45.5,
  "songIndex": 1,
  "timestamp": "2025-01-19T20:30:00.000Z"
}
```

### Operation Types

- **`pause`**: Pause current playback
- **`resume`**: Resume paused playback  
- **`skip`**: Skip to next song
- **`insert`**: Add new song to queue

## 🧪 Testing

The server includes built-in simulation functions for testing:

```javascript
// In the server console or via external script
const { simulatePause, simulateResume, simulateSkip, simulateInsert } = require('./websocket-server.js');

simulatePause();    // Broadcast pause command
simulateResume();   // Broadcast resume command
simulateSkip();     // Broadcast skip command
simulateInsert();   // Broadcast insert command
```

## 🔧 Configuration

### Environment Variables
- `PORT`: Server port (default: 3000)

### Unity Client Configuration
Update the WebSocket URL in your Unity scripts:
```csharp
public string serverUrl = "ws://localhost:3000";
```

## 📊 Monitoring

The server logs all connections, disconnections, and message broadcasts to the console.

### Log Format
```
[WEBSOCKET_SERVER] New client connected from ::1
[WEBSOCKET_SERVER] Broadcasting tracklist update: { operationType: 'pause', ... }
[WEBSOCKET_SERVER] Client disconnected
```

## 🔄 Integration with Unity

### Master Application
The master should broadcast tracklist updates when:
- User pauses/resumes playback
- User skips to next song
- New song is added to queue
- Song status changes

### Slave Application
The slave will:
- Connect to WebSocket server on startup
- Listen for tracklist updates
- Apply updates immediately to local playback
- Fall back to MongoDB polling if WebSocket disconnects

## 🛠️ Troubleshooting

### Common Issues

1. **Connection Refused**
   - Ensure server is running on correct port
   - Check firewall settings
   - Verify WebSocket URL in Unity

2. **Messages Not Received**
   - Check WebSocket connection status
   - Verify message format matches expected JSON structure
   - Check Unity console for error messages

3. **Server Crashes**
   - Check Node.js version compatibility
   - Verify all dependencies are installed
   - Check server logs for error details

### Debug Mode

Enable debug logging in Unity:
```csharp
webSocketClient.enableDebugLogs = true;
```

## 📝 License

MIT License - see LICENSE file for details.
