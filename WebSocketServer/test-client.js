const WebSocket = require('ws');

// Test WebSocket client to simulate Unity slave behavior
class TestWebSocketClient {
    constructor(url = 'ws://localhost:3000') {
        this.url = url;
        this.ws = null;
        this.reconnectAttempts = 0;
        this.maxReconnectAttempts = 5;
        this.reconnectInterval = 3000;
    }

    connect() {
        console.log(`[TEST_CLIENT] Connecting to ${this.url}...`);
        
        this.ws = new WebSocket(this.url);
        
        this.ws.on('open', () => {
            console.log('[TEST_CLIENT] Connected to WebSocket server');
            this.reconnectAttempts = 0;
        });
        
        this.ws.on('message', (data) => {
            try {
                const message = JSON.parse(data);
                console.log('[TEST_CLIENT] Received message:', message);
                
                // Simulate Unity slave behavior
                this.handleTracklistUpdate(message);
            } catch (error) {
                console.error('[TEST_CLIENT] Error parsing message:', error);
            }
        });
        
        this.ws.on('close', () => {
            console.log('[TEST_CLIENT] WebSocket connection closed');
            this.attemptReconnect();
        });
        
        this.ws.on('error', (error) => {
            console.error('[TEST_CLIENT] WebSocket error:', error);
        });
    }
    
    handleTracklistUpdate(message) {
        if (message.operationType) {
            console.log(`[TEST_CLIENT] Processing tracklist update: ${message.operationType}`);
            
            switch (message.operationType) {
                case 'pause':
                    console.log('[TEST_CLIENT] → Pausing playback...');
                    break;
                case 'resume':
                    console.log('[TEST_CLIENT] → Resuming playback...');
                    break;
                case 'skip':
                    console.log('[TEST_CLIENT] → Skipping to next song...');
                    break;
                case 'insert':
                    console.log('[TEST_CLIENT] → Adding new song to queue...');
                    break;
                default:
                    console.log(`[TEST_CLIENT] → Unknown operation: ${message.operationType}`);
            }
        }
    }
    
    attemptReconnect() {
        if (this.reconnectAttempts < this.maxReconnectAttempts) {
            this.reconnectAttempts++;
            console.log(`[TEST_CLIENT] Attempting to reconnect... (${this.reconnectAttempts}/${this.maxReconnectAttempts})`);
            
            setTimeout(() => {
                this.connect();
            }, this.reconnectInterval);
        } else {
            console.log('[TEST_CLIENT] Max reconnection attempts reached. Giving up.');
        }
    }
    
    send(message) {
        if (this.ws && this.ws.readyState === WebSocket.OPEN) {
            this.ws.send(JSON.stringify(message));
        } else {
            console.log('[TEST_CLIENT] Cannot send message - not connected');
        }
    }
    
    disconnect() {
        if (this.ws) {
            this.ws.close();
        }
    }
}

// Create and start test client
const testClient = new TestWebSocketClient();
testClient.connect();

// Handle process termination
process.on('SIGINT', () => {
    console.log('\n[TEST_CLIENT] Disconnecting...');
    testClient.disconnect();
    process.exit(0);
});

// Keep the process running
console.log('[TEST_CLIENT] Test client started. Press Ctrl+C to exit.');
console.log('[TEST_CLIENT] Waiting for tracklist updates from server...');
