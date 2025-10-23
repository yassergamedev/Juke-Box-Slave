const WebSocket = require('ws');

// Example of how the master Unity app should integrate with WebSocket
class MasterWebSocketIntegration {
    constructor(serverUrl = 'ws://localhost:3000') {
        this.serverUrl = serverUrl;
        this.ws = null;
        this.isConnected = false;
    }

    // Connect to WebSocket server
    connect() {
        console.log('[MASTER_INTEGRATION] Connecting to WebSocket server...');
        
        this.ws = new WebSocket(this.serverUrl);
        
        this.ws.on('open', () => {
            console.log('[MASTER_INTEGRATION] Connected to WebSocket server');
            this.isConnected = true;
        });
        
        this.ws.on('close', () => {
            console.log('[MASTER_INTEGRATION] WebSocket connection closed');
            this.isConnected = false;
        });
        
        this.ws.on('error', (error) => {
            console.error('[MASTER_INTEGRATION] WebSocket error:', error);
        });
    }

    // Broadcast pause command to all slaves
    broadcastPause(songTitle, currentTime) {
        this.broadcastUpdate({
            operationType: 'pause',
            songTitle: songTitle,
            status: 'paused',
            currentTime: currentTime
        });
    }

    // Broadcast resume command to all slaves
    broadcastResume(songTitle, currentTime) {
        this.broadcastUpdate({
            operationType: 'resume',
            songTitle: songTitle,
            status: 'playing',
            currentTime: currentTime
        });
    }

    // Broadcast skip command to all slaves
    broadcastSkip(nextSongTitle, songIndex) {
        this.broadcastUpdate({
            operationType: 'skip',
            songTitle: nextSongTitle,
            status: 'playing',
            songIndex: songIndex
        });
    }

    // Broadcast new song insertion to all slaves
    broadcastInsert(songTitle, songData) {
        this.broadcastUpdate({
            operationType: 'insert',
            songTitle: songTitle,
            status: 'queued',
            songId: songData.songId || 'song_' + Date.now(),
            artist: songData.artist || 'Unknown Artist',
            album: songData.album || 'Unknown Album',
            duration: songData.duration || 180,
            priority: songData.priority || 1,
            requestedBy: songData.requestedBy || 'user',
            masterId: songData.masterId || 'master',
            existsAtMaster: true
        });
    }

    // Generic broadcast method
    broadcastUpdate(update) {
        if (!this.isConnected || !this.ws) {
            console.log('[MASTER_INTEGRATION] Cannot broadcast - not connected to WebSocket server');
            return;
        }

        const message = JSON.stringify({
            ...update,
            timestamp: new Date().toISOString()
        });

        console.log('[MASTER_INTEGRATION] Broadcasting update:', update);
        this.ws.send(message);
    }

    // Disconnect from WebSocket server
    disconnect() {
        if (this.ws) {
            this.ws.close();
        }
    }
}

// Example usage (this would be integrated into your Unity master app)
function demonstrateMasterIntegration() {
    const masterIntegration = new MasterWebSocketIntegration();
    masterIntegration.connect();

    // Wait for connection, then demonstrate various operations
    setTimeout(() => {
        console.log('\n=== MASTER INTEGRATION DEMO ===');
        
        // Simulate user actions
        masterIntegration.broadcastInsert('New Song - Artist');
        setTimeout(() => masterIntegration.broadcastResume('Current Song', 0), 1000);
        setTimeout(() => masterIntegration.broadcastPause('Current Song', 45.5), 3000);
        setTimeout(() => masterIntegration.broadcastResume('Current Song', 45.5), 5000);
        setTimeout(() => masterIntegration.broadcastSkip('Next Song', 1), 7000);
        
    }, 2000);

    // Cleanup after demo
    setTimeout(() => {
        console.log('\n[MASTER_INTEGRATION] Demo completed. Disconnecting...');
        masterIntegration.disconnect();
        process.exit(0);
    }, 10000);
}

// Run demonstration if this file is executed directly
if (require.main === module) {
    demonstrateMasterIntegration();
}

module.exports = MasterWebSocketIntegration;
