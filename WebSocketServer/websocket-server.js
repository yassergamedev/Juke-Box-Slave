const WebSocket = require('ws');
const http = require('http');

// Create HTTP server
const server = http.createServer();

// Create WebSocket server
const wss = new WebSocket.Server({ server });

// Store connected clients
const clients = new Set();

// WebSocket server setup
wss.on('connection', (ws, req) => {
    console.log(`[WEBSOCKET_SERVER] New client connected from ${req.socket.remoteAddress}`);
    clients.add(ws);
    
    // Send welcome message
    ws.send(JSON.stringify({
        type: 'connection',
        message: 'Connected to Juke Box WebSocket Server',
        timestamp: new Date().toISOString()
    }));
    
    // Handle incoming messages
    ws.on('message', (message) => {
        try {
            const data = JSON.parse(message);
            console.log(`[WEBSOCKET_SERVER] Received message:`, data);
            
            // Echo back to sender
            ws.send(JSON.stringify({
                type: 'echo',
                originalMessage: data,
                timestamp: new Date().toISOString()
            }));
        } catch (error) {
            console.error(`[WEBSOCKET_SERVER] Error parsing message:`, error);
        }
    });
    
    // Handle client disconnect
    ws.on('close', () => {
        console.log(`[WEBSOCKET_SERVER] Client disconnected`);
        clients.delete(ws);
    });
    
    // Handle errors
    ws.on('error', (error) => {
        console.error(`[WEBSOCKET_SERVER] WebSocket error:`, error);
        clients.delete(ws);
    });
});

// Broadcast function for tracklist updates
function broadcastTracklistUpdate(update) {
    const message = JSON.stringify({
        operationType: update.operationType,
        songTitle: update.songTitle,
        status: update.status,
        currentTime: update.currentTime,
        songIndex: update.songIndex,
        timestamp: new Date().toISOString()
    });
    
    console.log(`[WEBSOCKET_SERVER] Broadcasting tracklist update:`, update);
    
    clients.forEach(client => {
        if (client.readyState === WebSocket.OPEN) {
            client.send(message);
        }
    });
}

// Example usage functions for testing
function simulatePause() {
    broadcastTracklistUpdate({
        operationType: 'pause',
        songTitle: 'Current Song',
        status: 'paused',
        currentTime: 45.5
    });
}

function simulateResume() {
    broadcastTracklistUpdate({
        operationType: 'resume',
        songTitle: 'Current Song',
        status: 'playing',
        currentTime: 45.5
    });
}

function simulateSkip() {
    broadcastTracklistUpdate({
        operationType: 'skip',
        songTitle: 'Next Song',
        status: 'playing',
        songIndex: 1
    });
}

function simulateInsert() {
    broadcastTracklistUpdate({
        operationType: 'insert',
        songTitle: 'New Song Added',
        status: 'queued',
        songId: 'song_' + Date.now(),
        artist: 'Test Artist',
        album: 'Test Album',
        duration: 180, // 3 minutes
        priority: 1,
        requestedBy: 'user',
        masterId: 'master',
        existsAtMaster: true
    });
}

// Start server
const PORT = process.env.PORT || 3000;
server.listen(PORT, () => {
    console.log(`[WEBSOCKET_SERVER] Server running on port ${PORT}`);
    console.log(`[WEBSOCKET_SERVER] WebSocket endpoint: ws://localhost:${PORT}`);
    console.log(`[WEBSOCKET_SERVER] Connected clients: ${clients.size}`);
});

// Export functions for external use
module.exports = {
    broadcastTracklistUpdate,
    simulatePause,
    simulateResume,
    simulateSkip,
    simulateInsert,
    getConnectedClients: () => clients.size
};

// Keep server running
process.on('SIGINT', () => {
    console.log('\n[WEBSOCKET_SERVER] Shutting down gracefully...');
    server.close(() => {
        console.log('[WEBSOCKET_SERVER] Server closed');
        process.exit(0);
    });
});
