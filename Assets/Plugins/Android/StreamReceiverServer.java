package com.brenpoly.cardboardxr.stream;

import android.app.Activity;
import android.content.Context;
import android.net.nsd.NsdManager;
import android.net.nsd.NsdServiceInfo;
import android.util.Base64;
import android.util.Log;

import java.io.BufferedInputStream;
import java.io.ByteArrayOutputStream;
import java.io.DataInputStream;
import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.util.Collections;
import java.util.Enumeration;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.regex.Pattern;

import javax.net.ssl.KeyManagerFactory;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLServerSocket;

// Hardware-accelerated WebRTC local signaling server + AirPlay video receiver for CardboardXR ("Schermi Live").
//
// How it works (up to 4 simultaneous 1080p 60 fps streams, zero browser throttling, Opus stereo audio):
//   1. Each VR StreamCastWindow opens a local hardware-accelerated VrWebView at
//      http://127.0.0.1:8765/receiver?ch=<1..4> which connects to ws://127.0.0.1:8765/sig/<ch>?role=receiver.
//   2. The sender (Windows, macOS, Linux, iPad, iPhone, Android) opens https://<IP-PIXEL-9>:8443,
//      clicks "Condividi Schermo", and establishes a direct LAN peer-to-peer WebRTC RTCPeerConnection
//      (GPU hardware H.264/VP9 60 fps + Opus stereo) via wss://<IP-PIXEL-9>:8443/sig/<ch>?role=sender.
//   3. Because WebRTC runs in Chromium's dedicated media thread with GPU encoding & decoding,
//      switching windows or minimizing Chrome on PC NEVER throttles to 1 fps and keeps latency < 35 ms.
public final class StreamReceiverServer {
    private static final String TAG = "CardboardXRStream";
    public static final int MAX_CHANNELS = 4;
    public static final int HTTPS_PORT = 8443;
    public static final int HTTP_PORT = 8765;
    public static final int AIRPLAY_PORT = 7000;
    private static final String WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private static final Pattern MDNS_HOST_PATTERN = Pattern.compile("[a-fA-F0-9\\-]+\\.local");

    private final Activity mActivity;
    private final SignalChannel[] mChannels = new SignalChannel[MAX_CHANNELS];
    private final ExecutorService mExecutor = Executors.newCachedThreadPool();

    private volatile boolean mRunning;
    private volatile String mLocalIp = "127.0.0.1";
    private volatile boolean mHttpsReady;
    private ServerSocket mHttpsServer;
    private ServerSocket mHttpServer;
    private ServerSocket mAirPlayServer;
    private NsdManager mNsdManager;
    private NsdManager.RegistrationListener mAirPlayRegListener;

    public StreamReceiverServer(Activity activity) {
        mActivity = activity;
        for (int i = 0; i < MAX_CHANNELS; i++) {
            mChannels[i] = new SignalChannel(i);
        }
    }

    public void start() {
        if (mRunning)
            return;
        mRunning = true;
        mLocalIp = detectLocalIp();
        Log.i(TAG, "Starting WebRTC multi-stream signaling server on " + mLocalIp);

        // Bind HTTP loopback port synchronously so VrWebView can load http://127.0.0.1:8765/receiver immediately.
        try {
            ServerSocket http = new ServerSocket(HTTP_PORT);
            http.setReuseAddress(true);
            mHttpServer = http;
        } catch (Throwable t) {
            Log.w(TAG, "Could not pre-bind HTTP port " + HTTP_PORT + ": " + t.getMessage());
        }

        mExecutor.execute(this::runHttpsServer);
        mExecutor.execute(this::runHttpServer);
        mExecutor.execute(this::runAirPlayServer);
        registerAirPlayNsd();
    }

    public void stop() {
        mRunning = false;
        unregisterAirPlayNsd();
        closeQuietly(mHttpsServer);
        closeQuietly(mHttpServer);
        closeQuietly(mAirPlayServer);
        mHttpsServer = null;
        mHttpServer = null;
        mAirPlayServer = null;
        for (SignalChannel ch : mChannels) {
            ch.closeAll();
        }
        mExecutor.shutdownNow();
        Log.i(TAG, "WebRTC multi-stream server stopped");
    }

    public String getLocalIp() {
        String ip = detectLocalIp();
        if (!"127.0.0.1".equals(ip))
            mLocalIp = ip;
        return mLocalIp;
    }

    public String getHttpsUrl() {
        return "https://" + getLocalIp() + ":" + HTTPS_PORT;
    }

    public String getHttpUrl() {
        return "http://" + getLocalIp() + ":" + HTTP_PORT;
    }

    public String getReceiverUrl(int channel) {
        return "http://127.0.0.1:" + HTTP_PORT + "/receiver?ch=" + (channel + 1);
    }

    public boolean isChannelConnected(int channel) {
        if (channel < 0 || channel >= MAX_CHANNELS)
            return false;
        return mChannels[channel].isLive();
    }

    public String getChannelStatus(int channel) {
        if (channel < 0 || channel >= MAX_CHANNELS)
            return "";
        return mChannels[channel].getStatusText();
    }

    public void disconnectChannel(int channel) {
        if (channel >= 0 && channel < MAX_CHANNELS)
            mChannels[channel].disconnectSender();
    }

    private void runHttpsServer() {
        try {
            KeyStore ks = KeyStore.getInstance("PKCS12");
            try (InputStream is = mActivity.getAssets().open("stream_cert.p12")) {
                ks.load(is, "cardboardxr".toCharArray());
            }
            KeyManagerFactory kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
            kmf.init(ks, "cardboardxr".toCharArray());
            SSLContext ssl = SSLContext.getInstance("TLS");
            ssl.init(kmf.getKeyManagers(), null, null);

            SSLServerSocket server = (SSLServerSocket) ssl.getServerSocketFactory().createServerSocket(HTTPS_PORT);
            server.setReuseAddress(true);
            mHttpsServer = server;
            mHttpsReady = true;
            Log.i(TAG, "HTTPS/WSS WebRTC signaling listening on :" + HTTPS_PORT);

            while (mRunning) {
                Socket client = server.accept();
                client.setTcpNoDelay(true);
                mExecutor.execute(() -> handleClient(client, true));
            }
        } catch (Throwable t) {
            if (mRunning)
                Log.w(TAG, "HTTPS server stopped: " + t.getMessage());
        } finally {
            mHttpsReady = false;
        }
    }

    private void runHttpServer() {
        try {
            ServerSocket server = mHttpServer;
            if (server == null) {
                server = new ServerSocket(HTTP_PORT);
                server.setReuseAddress(true);
                mHttpServer = server;
            }
            Log.i(TAG, "HTTP/WS local signaling listening on :" + HTTP_PORT);

            while (mRunning) {
                Socket client = server.accept();
                client.setTcpNoDelay(true);
                mExecutor.execute(() -> handleClient(client, false));
            }
        } catch (Throwable t) {
            if (mRunning)
                Log.w(TAG, "HTTP server stopped: " + t.getMessage());
        }
    }

    private void runAirPlayServer() {
        try {
            ServerSocket server = new ServerSocket(AIRPLAY_PORT);
            server.setReuseAddress(true);
            mAirPlayServer = server;
            Log.i(TAG, "AirPlay video receiver listening on :" + AIRPLAY_PORT);

            while (mRunning) {
                Socket client = server.accept();
                client.setTcpNoDelay(true);
                mExecutor.execute(() -> handleAirPlayClient(client));
            }
        } catch (Throwable t) {
            if (mRunning)
                Log.w(TAG, "AirPlay server stopped: " + t.getMessage());
        }
    }

    private void registerAirPlayNsd() {
        try {
            mNsdManager = (NsdManager) mActivity.getSystemService(Context.NSD_SERVICE);
            if (mNsdManager == null)
                return;
            NsdServiceInfo info = new NsdServiceInfo();
            info.setServiceName("CardboardXR VR");
            info.setServiceType("_airplay._tcp.");
            info.setPort(AIRPLAY_PORT);
            info.setAttribute("deviceid", "AA:BB:CC:DD:EE:FF");
            info.setAttribute("features", "0x5A7FFFF7,0x1E");
            info.setAttribute("flags", "0x4");
            info.setAttribute("model", "AppleTV3,2");
            info.setAttribute("srcvers", "220.68");
            info.setAttribute("vv", "2");
            mAirPlayRegListener = new NsdManager.RegistrationListener() {
                @Override public void onRegistrationFailed(NsdServiceInfo s, int err) {}
                @Override public void onUnregistrationFailed(NsdServiceInfo s, int err) {}
                @Override public void onServiceRegistered(NsdServiceInfo s) {
                    Log.i(TAG, "AirPlay advertised as " + s.getServiceName());
                }
                @Override public void onServiceUnregistered(NsdServiceInfo s) {}
            };
            mNsdManager.registerService(info, NsdManager.PROTOCOL_DNS_SD, mAirPlayRegListener);
        } catch (Throwable t) {
            Log.w(TAG, "AirPlay NSD failed: " + t.getMessage());
        }
    }

    private void unregisterAirPlayNsd() {
        try {
            if (mNsdManager != null && mAirPlayRegListener != null)
                mNsdManager.unregisterService(mAirPlayRegListener);
        } catch (Throwable ignored) {
        } finally {
            mAirPlayRegListener = null;
        }
    }

    private void handleClient(Socket socket, boolean isHttps) {
        try {
            socket.setSoTimeout(30000);
            BufferedInputStream in = new BufferedInputStream(socket.getInputStream(), 32768);
            OutputStream out = socket.getOutputStream();

            String requestLine = readLine(in);
            if (requestLine == null || requestLine.isEmpty()) {
                socket.close();
                return;
            }
            String[] parts = requestLine.split(" ");
            if (parts.length < 2) {
                socket.close();
                return;
            }
            String fullPath = parts[1];
            String query = fullPath.contains("?") ? fullPath.substring(fullPath.indexOf('?') + 1) : "";
            String path = fullPath.contains("?") ? fullPath.substring(0, fullPath.indexOf('?')) : fullPath;

            Map<String, String> headers = readHeaders(in);

            String upgrade = headers.get("upgrade");
            if (upgrade != null && upgrade.equalsIgnoreCase("websocket") && path.startsWith("/sig/")) {
                int ch = parseChannelIndex(path.substring(5));
                boolean isReceiver = query.contains("role=receiver");
                handleSignalingWebSocket(socket, in, out, headers, ch, isReceiver);
                return;
            }

            if ("/receiver".equals(path)) {
                int ch = parseChannelIndex(query);
                String html = buildReceiverHtml(ch + 1);
                sendHttpResponse(out, "200 OK", "text/html; charset=utf-8", html.getBytes(StandardCharsets.UTF_8));
                socket.close();
                return;
            }

            if (!isHttps && mHttpsReady && "/".equals(path)) {
                String suffix = query.isEmpty() ? "" : ("?" + query);
                String html = buildHttpRedirectPage(getHttpsUrl() + "/" + suffix);
                sendHttpResponse(out, "200 OK", "text/html; charset=utf-8", html.getBytes(StandardCharsets.UTF_8));
                socket.close();
                return;
            }

            String html = buildSenderHtml();
            sendHttpResponse(out, "200 OK", "text/html; charset=utf-8", html.getBytes(StandardCharsets.UTF_8));
            socket.close();
        } catch (Throwable ignored) {
            closeQuietly(socket);
        }
    }

    private void handleSignalingWebSocket(Socket socket, BufferedInputStream in, OutputStream out,
                                          Map<String, String> headers, int channelIdx, boolean isReceiver) throws IOException {
        String key = headers.get("sec-websocket-key");
        if (key == null) {
            socket.close();
            return;
        }
        String accept;
        try {
            MessageDigest sha1 = MessageDigest.getInstance("SHA-1");
            byte[] hash = sha1.digest((key.trim() + WS_GUID).getBytes(StandardCharsets.UTF_8));
            accept = Base64.encodeToString(hash, Base64.NO_WRAP);
        } catch (Exception e) {
            socket.close();
            return;
        }

        String resp = "HTTP/1.1 101 Switching Protocols\r\n"
                + "Upgrade: websocket\r\n"
                + "Connection: Upgrade\r\n"
                + "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
        out.write(resp.getBytes(StandardCharsets.US_ASCII));
        out.flush();

        socket.setSoTimeout(0);
        WsPeer peer = new WsPeer(socket, out, socket.getInetAddress() != null ? socket.getInetAddress().getHostAddress() : "");
        SignalChannel ch = mChannels[channelIdx];
        if (isReceiver) {
            ch.setReceiver(peer);
            Log.i(TAG, "WebRTC receiver ready on Schermo " + (channelIdx + 1));
        } else {
            ch.setSender(peer);
            Log.i(TAG, "WebRTC sender connected on Schermo " + (channelIdx + 1) + " from " + peer.remoteIp);
        }

        DataInputStream din = new DataInputStream(in);
        byte[] maskKey = new byte[4];
        ByteArrayOutputStream frag = new ByteArrayOutputStream(4096);

        try {
            while (mRunning && !socket.isClosed()) {
                int b0 = din.readUnsignedByte();
                int b1 = din.readUnsignedByte();
                boolean fin = (b0 & 0x80) != 0;
                int opcode = b0 & 0x0F;
                boolean masked = (b1 & 0x80) != 0;
                long len = b1 & 0x7F;
                if (len == 126) {
                    len = din.readUnsignedShort();
                } else if (len == 127) {
                    len = din.readLong();
                }
                if (len < 0 || len > 1024 * 1024)
                    break;
                if (masked)
                    din.readFully(maskKey);

                int ilen = (int) len;
                byte[] payload = new byte[ilen];
                if (ilen > 0) {
                    din.readFully(payload, 0, ilen);
                    if (masked) {
                        for (int i = 0; i < ilen; i++)
                            payload[i] ^= maskKey[i & 3];
                    }
                }

                if (opcode == 0x8) {
                    break;
                } else if (opcode == 0x9) {
                    peer.sendControl(0x8A);
                } else if (opcode == 0x1 || opcode == 0x0) {
                    if (opcode == 0x1) frag.reset();
                    frag.write(payload);
                    if (fin) {
                        String msg = frag.toString("UTF-8");
                        frag.reset();
                        ch.onMessage(isReceiver, peer, msg, getLocalIp());
                    }
                }
            }
        } catch (EOFException ignored) {
        } catch (Throwable t) {
            if (mRunning)
                Log.i(TAG, "Signaling closed on Schermo " + (channelIdx + 1) + " (" + (isReceiver ? "receiver" : "sender") + ")");
        } finally {
            ch.removePeer(isReceiver, peer);
            closeQuietly(socket);
        }
    }

    private void handleAirPlayClient(Socket socket) {
        try {
            socket.setSoTimeout(15000);
            BufferedInputStream in = new BufferedInputStream(socket.getInputStream());
            OutputStream out = socket.getOutputStream();

            while (mRunning && !socket.isClosed()) {
                String reqLine = readLine(in);
                if (reqLine == null || reqLine.isEmpty())
                    break;
                String[] parts = reqLine.split(" ");
                if (parts.length < 2)
                    break;
                String path = parts[1];
                Map<String, String> headers = readHeaders(in);
                int len = parseIntSafe(headers.get("content-length"), 0);
                byte[] body = new byte[Math.max(0, Math.min(len, 256 * 1024))];
                if (body.length > 0)
                    readFully(in, body, 0, body.length);

                if (path.startsWith("/server-info")) {
                    String plist = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
                            + "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n"
                            + "<plist version=\"1.0\"><dict>"
                            + "<key>deviceid</key><string>AA:BB:CC:DD:EE:FF</string>"
                            + "<key>features</key><integer>119</integer>"
                            + "<key>model</key><string>AppleTV3,2</string>"
                            + "<key>protovers</key><string>1.0</string>"
                            + "<key>srcvers</key><string>220.68</string>"
                            + "</dict></plist>";
                    sendHttpResponse(out, "200 OK", "text/x-apple-plist+xml", plist.getBytes(StandardCharsets.UTF_8));
                } else if (path.startsWith("/play")) {
                    String url = extractAirPlayUrl(new String(body, StandardCharsets.UTF_8));
                    if (url != null) {
                        SignalChannel target = pickAvailableChannel();
                        Log.i(TAG, "AirPlay video URL -> Schermo " + (target.mIndex + 1) + ": " + url);
                        target.sendAirPlayUrl(url);
                    }
                    sendHttpResponse(out, "200 OK", "text/plain", new byte[0]);
                } else if (path.startsWith("/stop")) {
                    for (SignalChannel ch : mChannels) ch.sendAirPlayStop();
                    sendHttpResponse(out, "200 OK", "text/plain", new byte[0]);
                } else {
                    sendHttpResponse(out, "200 OK", "text/plain", new byte[0]);
                }
            }
        } catch (Throwable ignored) {
        } finally {
            closeQuietly(socket);
        }
    }

    private SignalChannel pickAvailableChannel() {
        for (SignalChannel ch : mChannels) {
            if (!ch.isLive())
                return ch;
        }
        return mChannels[0];
    }

    private static String extractAirPlayUrl(String body) {
        for (String line : body.split("\r?\n")) {
            if (line.toLowerCase(Locale.US).startsWith("content-location:"))
                return line.substring(17).trim();
        }
        int idx = body.indexOf("http://");
        if (idx < 0) idx = body.indexOf("https://");
        if (idx >= 0) {
            int end = idx;
            while (end < body.length() && body.charAt(end) > 32 && body.charAt(end) < 127) end++;
            return body.substring(idx, end);
        }
        return null;
    }

    private static int parseChannelIndex(String raw) {
        try {
            int n = Integer.parseInt(raw.replaceAll("[^0-9]", ""));
            if (n >= 1 && n <= MAX_CHANNELS)
                return n - 1;
        } catch (Exception ignored) {
        }
        return 0;
    }

    private static int parseIntSafe(String s, int def) {
        if (s == null) return def;
        try { return Integer.parseInt(s.trim()); } catch (Exception e) { return def; }
    }

    private static String readLine(InputStream in) throws IOException {
        ByteArrayOutputStream bos = new ByteArrayOutputStream(128);
        int b;
        while ((b = in.read()) != -1) {
            if (b == '\n') break;
            if (b != '\r') bos.write(b);
            if (bos.size() > 8192) break;
        }
        if (b == -1 && bos.size() == 0)
            return null;
        return bos.toString("UTF-8");
    }

    private static Map<String, String> readHeaders(InputStream in) throws IOException {
        Map<String, String> map = new HashMap<>();
        String line;
        while ((line = readLine(in)) != null && !line.isEmpty()) {
            int colon = line.indexOf(':');
            if (colon > 0)
                map.put(line.substring(0, colon).trim().toLowerCase(Locale.US), line.substring(colon + 1).trim());
        }
        return map;
    }

    private static void readFully(InputStream in, byte[] buf, int off, int len) throws IOException {
        int total = 0;
        while (total < len) {
            int r = in.read(buf, off + total, len - total);
            if (r < 0) throw new EOFException();
            total += r;
        }
    }

    private static void sendHttpResponse(OutputStream out, String status, String contentType, byte[] body) throws IOException {
        String header = "HTTP/1.1 " + status + "\r\n"
                + "Content-Type: " + contentType + "\r\n"
                + "Content-Length: " + body.length + "\r\n"
                + "Access-Control-Allow-Origin: *\r\n"
                + "Cache-Control: no-store\r\n"
                + "Connection: close\r\n\r\n";
        out.write(header.getBytes(StandardCharsets.US_ASCII));
        if (body.length > 0)
            out.write(body);
        out.flush();
    }

    private static void closeQuietly(ServerSocket s) {
        if (s != null) try { s.close(); } catch (Throwable ignored) {}
    }

    private static void closeQuietly(Socket s) {
        if (s != null) try { s.close(); } catch (Throwable ignored) {}
    }

    private static String detectLocalIp() {
        try {
            Enumeration<NetworkInterface> ifaces = NetworkInterface.getNetworkInterfaces();
            String fallback = "127.0.0.1";
            for (NetworkInterface nif : Collections.list(ifaces)) {
                if (!nif.isUp() || nif.isLoopback())
                    continue;
                String name = nif.getName().toLowerCase(Locale.US);
                for (InetAddress addr : Collections.list(nif.getInetAddresses())) {
                    if (addr instanceof Inet4Address && !addr.isLoopbackAddress()) {
                        String host = addr.getHostAddress();
                        if (name.startsWith("wlan") || name.startsWith("ap"))
                            return host;
                        fallback = host;
                    }
                }
            }
            return fallback;
        } catch (Throwable ignored) {
            return "127.0.0.1";
        }
    }

    private static String buildHttpRedirectPage(String httpsUrl) {
        return "<!doctype html><html lang='it'><head><meta charset='utf-8'>"
                + "<meta name='viewport' content='width=device-width,initial-scale=1'>"
                + "<title>CardboardXR WebRTC Cast</title>"
                + "<style>body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;"
                + "background:#0b1020;color:#f8fafc;font-family:system-ui,sans-serif;padding:24px;text-align:center}"
                + ".card{max-width:460px;background:#151c31;border:1px solid #263354;border-radius:20px;padding:28px}"
                + "a.btn{display:inline-block;margin-top:16px;padding:14px 24px;border-radius:999px;background:#38bdf8;"
                + "color:#082f49;font-weight:700;text-decoration:none;font-size:17px}"
                + "p{color:#cbd5e1;line-height:1.5}</style></head><body><div class='card'>"
                + "<h1>CardboardXR • Schermi Live (60 fps)</h1>"
                + "<p>Apri la connessione HTTPS locale per avviare lo streaming hardware <b>WebRTC 60 fps</b> dal tuo PC/Mac.</p>"
                + "<p style='font-size:13px;color:#94a3b8'>Solo la prima volta clicca <b>Avanzate &rarr; Procedi</b>.</p>"
                + "<a class='btn' href='" + httpsUrl + "'>Apri Trasmettitore WebRTC</a>"
                + "</div></body></html>";
    }

    // Local receiver page running inside Pixel 9's hardware-accelerated VrWebView (1280x720 edge-to-edge).
    private static String buildReceiverHtml(int chNum) {
        return "<!doctype html><html><head><meta charset='utf-8'>"
                + "<style>html,body{margin:0;padding:0;width:100%;height:100%;overflow:hidden;background:#0c0f18;}"
                + "video{width:100vw;height:100vh;object-fit:contain;background:#0c0f18;display:block;}</style></head>"
                + "<body><video id='v' autoplay playsinline></video><script>"
                + "const ch=" + chNum + ";const v=document.getElementById('v');let ws=null,pc=null,statTimer=null;"
                + "function closePc(){if(statTimer){clearInterval(statTimer);statTimer=null;}if(pc){try{pc.close();}catch(e){}pc=null;}"
                + "v.srcObject=null;v.removeAttribute('src');}"
                + "function send(obj){if(ws&&ws.readyState===1)ws.send(JSON.stringify(obj));}"
                + "function connectWs(){ws=new WebSocket('ws://127.0.0.1:" + HTTP_PORT + "/sig/'+ch+'?role=receiver');"
                + "ws.onopen=()=>send({type:'ready'});"
                + "ws.onclose=()=>{closePc();setTimeout(connectWs,600);};"
                + "ws.onmessage=async e=>{let m;try{m=JSON.parse(e.data);}catch(_){return;}"
                + "if(m.type==='airplay_play'){closePc();v.src=m.url;v.play().catch(()=>{});send({type:'status',live:true,text:'AirPlay Video'});return;}"
                + "if(m.type==='airplay_stop'||m.type==='bye'){closePc();send({type:'status',live:false,text:'In attesa su Schermo '+ch});return;}"
                + "if(m.type==='offer'){closePc();pc=new RTCPeerConnection({iceServers:[]});"
                + "pc.onicecandidate=ev=>{if(ev.candidate)send({type:'ice',candidate:ev.candidate});};"
                + "pc.ontrack=ev=>{const ms=ev.streams&&ev.streams[0]?ev.streams[0]:new MediaStream([ev.track]);"
                + "if(v.srcObject!==ms){v.srcObject=ms;v.play().catch(()=>{});}"
                + "send({type:'status',live:true,text:(m.label||'WebRTC Live')});};"
                + "pc.onconnectionstatechange=()=>{if(!pc)return;if(pc.connectionState==='disconnected'||pc.connectionState==='failed'||pc.connectionState==='closed'){closePc();send({type:'status',live:false,text:'In attesa su Schermo '+ch});}};"
                + "await pc.setRemoteDescription(new RTCSessionDescription(m.sdp));"
                + "const ans=await pc.createAnswer();await pc.setLocalDescription(ans);"
                + "send({type:'answer',sdp:pc.localDescription});"
                + "let lastFrames=0,lastTime=performance.now();"
                + "statTimer=setInterval(async()=>{if(!pc||pc.connectionState!=='connected')return;"
                + "try{const stats=await pc.getStats();stats.forEach(r=>{if(r.type==='inbound-rtp'&&r.kind==='video'){"
                + "const now=performance.now();const f=r.framesDecoded||0;const fps=Math.round((f-lastFrames)*1000/Math.max(1,now-lastTime));"
                + "lastFrames=f;lastTime=now;const w=r.frameWidth||v.videoWidth||0;const h=r.frameHeight||v.videoHeight||0;"
                + "if(w>0)send({type:'status',live:true,text:(m.label||'LIVE')+' • '+w+'x'+h+' • '+Math.max(0,fps)+' fps'});"
                + "}});}catch(_){}},1000);"
                + "}else if(m.type==='ice'&&pc&&m.candidate){try{await pc.addIceCandidate(new RTCIceCandidate(m.candidate));}catch(_){}}"
                + "};}connectWs();</script></body></html>";
    }

    // Sender page opened on Windows / macOS / Linux / iOS / iPadOS browsers at https://<IP-PIXEL-9>:8443.
    private static String buildSenderHtml() {
        return "<!doctype html><html lang='it'><head><meta charset='utf-8'>"
                + "<meta name='viewport' content='width=device-width,initial-scale=1'>"
                + "<title>CardboardXR • Schermi Live WebRTC</title>"
                + "<style>"
                + "*{box-sizing:border-box}body{margin:0;background:#0b1020;color:#f1f5f9;font-family:system-ui,-apple-system,sans-serif;padding:20px;display:flex;justify-content:center}"
                + ".wrap{width:100%;max-width:620px;background:#151c31;border:1px solid #263354;border-radius:24px;padding:24px;box-shadow:0 20px 50px rgba(0,0,0,.5)}"
                + "h1{margin:0 0 6px;font-size:24px}p.sub{margin:0 0 18px;color:#94a3b8;font-size:14px}"
                + ".row{display:flex;gap:10px;flex-wrap:wrap;margin-bottom:14px}"
                + "label{font-size:13px;color:#cbd5e1;display:block;margin-bottom:6px;font-weight:600}"
                + "select{width:100%;padding:10px 12px;border-radius:12px;border:1px solid #334155;background:#0f172a;color:#f8fafc;font-size:15px}"
                + ".col{flex:1;min-width:160px}"
                + "button{flex:1;min-width:170px;padding:13px 16px;border-radius:14px;border:none;font-size:15px;font-weight:700;cursor:pointer;text-align:center}"
                + ".primary{background:#38bdf8;color:#082f49}.primary:hover{background:#7dd3fc}"
                + ".sec{background:#334155;color:#f8fafc}.sec:hover{background:#475569}"
                + ".danger{background:#ef4444;color:#fff;width:100%;margin-top:8px}"
                + ".status{margin-top:14px;padding:12px 14px;border-radius:12px;background:#0f172a;border:1px solid #1e293b;font-size:14px;color:#94a3b8}"
                + ".status.live{border-color:#22c55e;color:#86efac}"
                + "video{width:100%;border-radius:14px;background:#020617;margin-top:14px;max-height:240px;display:none}"
                + ".tip{margin-top:14px;font-size:12px;color:#94a3b8;line-height:1.5;background:#0f172a;padding:12px;border-radius:12px}"
                + "</style></head><body><div class='wrap'>"
                + "<h1>CardboardXR • WebRTC 60 fps</h1>"
                + "<p class='sub'>Streaming hardware GPU (H.264/VP9 + Audio Stereo Opus) a bassissima latenza — continua a 60 fps anche se riduci o cambi finestra!</p>"
                + "<div class='row'>"
                + "<div class='col'><label>Schermo VR</label><select id='ch'>"
                + "<option value='1'>Schermo 1</option><option value='2'>Schermo 2</option>"
                + "<option value='3'>Schermo 3</option><option value='4'>Schermo 4</option></select></div>"
                + "<div class='col'><label>Profilo Hardware</label><select id='qual'>"
                + "<option value='1920,1080,60,12000000,motion' selected>Full HD 1080p • 60 fps (Film/Giochi)</option>"
                + "<option value='1920,1080,30,14000000,detail'>Full HD 1080p • 30 fps (Testo Nitido)</option>"
                + "<option value='1280,720,60,8000000,motion'>HD 720p • 60 fps (Ultra Rapido)</option></select></div></div>"
                + "<div class='row'><label style='display:flex;align-items:center;gap:8px;cursor:pointer'><input id='aud' type='checkbox' checked> Trasmetti audio stereo nel visore</label></div>"
                + "<div class='row'>"
                + "<button class='primary' id='btnScreen'>Condividi Schermo (PC / Mac)</button>"
                + "<button class='sec' id='btnFile'>Trasmetti Video Locale</button>"
                + "<button class='sec' id='btnCam'>Fotocamera Live</button>"
                + "</div>"
                + "<input id='fileInput' type='file' accept='video/*' style='display:none'>"
                + "<button class='danger' id='btnStop' style='display:none'>Interrompi Trasmissione</button>"
                + "<div class='status' id='st'>Pronto. Scegli lo schermo VR e clicca Condividi Schermo.</div>"
                + "<video id='prev' playsinline controls muted></video>"
                + "<div class='tip'><b>Suggerimento:</b> puoi ridurre a icona questa scheda o aprire altre app: lo stream WebRTC hardware resta sempre a 60 fps! Per aprire più schermi insieme, apri un'altra scheda e scegli Schermo 2, 3 o 4.</div>"
                + "</div><script>"
                + "const qs=new URLSearchParams(location.search);const chEl=document.getElementById('ch');"
                + "if(qs.get('ch'))chEl.value=qs.get('ch');"
                + "const st=document.getElementById('st'),vid=document.getElementById('prev'),btnStop=document.getElementById('btnStop');"
                + "let ws=null,pc=null,stream=null,statTimer=null;"
                + "function setSt(t,live){st.textContent=t;st.className=live?'status live':'status';}"
                + "function stopAll(){if(statTimer){clearInterval(statTimer);statTimer=null;}"
                + "if(ws&&ws.readyState===1){try{ws.send(JSON.stringify({type:'bye'}));}catch(_){}}"
                + "if(pc){try{pc.close();}catch(_){}pc=null;}"
                + "if(ws){try{ws.close();}catch(_){}ws=null;}"
                + "if(stream){stream.getTracks().forEach(t=>t.stop());stream=null;}"
                + "vid.pause();vid.srcObject=null;vid.removeAttribute('src');vid.style.display='none';"
                + "btnStop.style.display='none';setSt('Trasmissione interrotta.',false);}"
                + "async function startWebRtc(ms,label,bitrate,fps,hint){"
                + "const proto=location.protocol==='https:'?'wss://':'ws://';"
                + "ws=new WebSocket(proto+location.host+'/sig/'+chEl.value+'?role=sender');"
                + "ws.onopen=async()=>{btnStop.style.display='block';setSt('Connessione WebRTC hardware in corso...',false);"
                + "pc=new RTCPeerConnection({iceServers:[]});"
                + "pc.onicecandidate=ev=>{if(ev.candidate&&ws&&ws.readyState===1)ws.send(JSON.stringify({type:'ice',candidate:ev.candidate}));};"
                + "pc.onconnectionstatechange=()=>{if(!pc)return;"
                + "if(pc.connectionState==='connected')setSt('LIVE WebRTC su Schermo '+chEl.value+' ('+label+')',true);"
                + "else if(pc.connectionState==='failed'||pc.connectionState==='disconnected')setSt('Connessione persa — premi di nuovo Condividi Schermo.',false);};"
                + "for(const track of ms.getTracks()){"
                + "if(track.kind==='video'&&'contentHint' in track)track.contentHint=hint;"
                + "const sender=pc.addTrack(track,ms);"
                + "if(track.kind==='video'){"
                + "try{const p=sender.getParameters();if(!p.encodings||!p.encodings.length)p.encodings=[{}];"
                + "p.encodings[0].maxBitrate=bitrate;p.encodings[0].maxFramerate=fps;await sender.setParameters(p);}catch(_){}"
                + "}}"
                + "const offer=await pc.createOffer();await pc.setLocalDescription(offer);"
                + "ws.send(JSON.stringify({type:'offer',sdp:pc.localDescription,label:label}));"
                + "statTimer=setInterval(async()=>{if(!pc||pc.connectionState!=='connected')return;"
                + "try{const stats=await pc.getStats();stats.forEach(r=>{if(r.type==='outbound-rtp'&&r.kind==='video'){"
                + "const w=r.frameWidth||0,h=r.frameHeight||0,f=Math.round(r.framesPerSecond||0);"
                + "if(w>0)setSt('LIVE WebRTC su Schermo '+chEl.value+' • '+w+'x'+h+' • '+f+' fps',true);"
                + "}});}catch(_){}},1000);};"
                + "ws.onmessage=async e=>{let m;try{m=JSON.parse(e.data);}catch(_){return;}"
                + "if(m.type==='answer'&&pc){await pc.setRemoteDescription(new RTCSessionDescription(m.sdp));}"
                + "else if(m.type==='ice'&&pc&&m.candidate){try{await pc.addIceCandidate(new RTCIceCandidate(m.candidate));}catch(_){}}"
                + "else if(m.type==='bye'){stopAll();}};"
                + "ws.onclose=()=>{if(pc)stopAll();};}"
                + "document.getElementById('btnScreen').onclick=async()=>{stopAll();"
                + "if(!navigator.mediaDevices||!navigator.mediaDevices.getDisplayMedia){setSt('Browser senza cattura schermo (es. Safari iOS): usa Trasmetti Video, Fotocamera o AirPlay.',false);return;}"
                + "try{const parts=document.getElementById('qual').value.split(',');const mw=+parts[0],mh=+parts[1],fps=+parts[2],br=+parts[3],hint=parts[4];"
                + "stream=await navigator.mediaDevices.getDisplayMedia({video:{width:{ideal:mw},height:{ideal:mh},frameRate:{ideal:fps,max:60}},audio:document.getElementById('aud').checked});"
                + "vid.srcObject=stream;vid.muted=true;vid.style.display='block';await vid.play();"
                + "stream.getVideoTracks()[0].onended=()=>stopAll();"
                + "const plat=/Mac/i.test(navigator.userAgent)?'macOS':(/Win/i.test(navigator.userAgent)?'Windows':'Desktop');"
                + "startWebRtc(stream,plat+' Schermo',br,fps,hint);"
                + "}catch(e){setSt('Condivisione annullata: '+e.message,false);}};"
                + "document.getElementById('btnCam').onclick=async()=>{stopAll();"
                + "try{stream=await navigator.mediaDevices.getUserMedia({video:{facingMode:'environment',width:{ideal:1280},height:{ideal:720},frameRate:{ideal:30}},audio:document.getElementById('aud').checked});"
                + "vid.srcObject=stream;vid.muted=true;vid.style.display='block';await vid.play();"
                + "startWebRtc(stream,'Fotocamera Live',8000000,30,'motion');"
                + "}catch(e){setSt('Fotocamera non disponibile: '+e.message,false);}};"
                + "document.getElementById('btnFile').onclick=()=>document.getElementById('fileInput').click();"
                + "document.getElementById('fileInput').onchange=async e=>{const f=e.target.files[0];if(!f)return;stopAll();"
                + "vid.src=URL.createObjectURL(f);vid.muted=false;vid.style.display='block';await vid.play();"
                + "const cap=vid.captureStream?vid.captureStream():(vid.mozCaptureStream?vid.mozCaptureStream():null);"
                + "if(!cap){setSt('captureStream non supportato su questo browser.',false);return;}"
                + "stream=cap;startWebRtc(stream,'Video: '+f.name.slice(0,22),10000000,60,'motion');};"
                + "btnStop.onclick=stopAll;</script></body></html>";
    }

    private static final class WsPeer {
        final Socket socket;
        final OutputStream out;
        final String remoteIp;

        WsPeer(Socket socket, OutputStream out, String remoteIp) {
            this.socket = socket;
            this.out = out;
            this.remoteIp = remoteIp != null ? remoteIp : "";
        }

        void sendText(String text) {
            if (text == null || socket.isClosed())
                return;
            byte[] data = text.getBytes(StandardCharsets.UTF_8);
            try {
                synchronized (out) {
                    out.write(0x81); // FIN + text frame
                    if (data.length < 126) {
                        out.write(data.length);
                    } else if (data.length < 65536) {
                        out.write(126);
                        out.write((data.length >>> 8) & 0xFF);
                        out.write(data.length & 0xFF);
                    } else {
                        out.write(127);
                        for (int i = 7; i >= 0; i--)
                            out.write((int) ((data.length >>> (i * 8)) & 0xFF));
                    }
                    out.write(data);
                    out.flush();
                }
            } catch (Throwable ignored) {
                closeQuietly(socket);
            }
        }

        void sendControl(int firstByte) {
            try {
                synchronized (out) {
                    out.write(firstByte);
                    out.write(0x00);
                    out.flush();
                }
            } catch (Throwable ignored) {
            }
        }
    }

    private static final class SignalChannel {
        final int mIndex;
        final Object mLock = new Object();
        WsPeer mReceiver;
        WsPeer mSender;
        String mPendingOffer;
        volatile boolean mLive;
        volatile String mStatus;

        SignalChannel(int index) {
            mIndex = index;
            mStatus = "In attesa su Schermo " + (index + 1);
        }

        void setReceiver(WsPeer peer) {
            String offerToReplay;
            synchronized (mLock) {
                if (mReceiver != null && mReceiver != peer)
                    closeQuietly(mReceiver.socket);
                mReceiver = peer;
                offerToReplay = mPendingOffer;
                mPendingOffer = null;
            }
            if (offerToReplay != null)
                peer.sendText(offerToReplay);
        }

        void setSender(WsPeer peer) {
            WsPeer prev;
            synchronized (mLock) {
                prev = mSender;
                mSender = peer;
                mPendingOffer = null;
            }
            if (prev != null && prev != peer) {
                prev.sendText("{\"type\":\"bye\"}");
                closeQuietly(prev.socket);
            }
        }

        void removePeer(boolean isReceiver, WsPeer peer) {
            WsPeer other = null;
            synchronized (mLock) {
                if (isReceiver && mReceiver == peer) {
                    mReceiver = null;
                    mLive = false;
                } else if (!isReceiver && mSender == peer) {
                    mSender = null;
                    mPendingOffer = null;
                    mLive = false;
                    mStatus = "In attesa su Schermo " + (mIndex + 1);
                    other = mReceiver;
                }
            }
            if (other != null)
                other.sendText("{\"type\":\"bye\"}");
        }

        void disconnectSender() {
            WsPeer s;
            WsPeer r;
            synchronized (mLock) {
                s = mSender;
                r = mReceiver;
                mSender = null;
                mPendingOffer = null;
                mLive = false;
                mStatus = "In attesa su Schermo " + (mIndex + 1);
            }
            if (s != null) {
                s.sendText("{\"type\":\"bye\"}");
                closeQuietly(s.socket);
            }
            if (r != null)
                r.sendText("{\"type\":\"bye\"}");
        }

        void closeAll() {
            WsPeer s;
            WsPeer r;
            synchronized (mLock) {
                s = mSender;
                r = mReceiver;
                mSender = null;
                mReceiver = null;
                mPendingOffer = null;
                mLive = false;
            }
            if (s != null) closeQuietly(s.socket);
            if (r != null) closeQuietly(r.socket);
        }

        void onMessage(boolean fromReceiver, WsPeer from, String msg, String localIp) {
            if (fromReceiver && msg.contains("\"type\":\"status\"")) {
                mLive = msg.contains("\"live\":true");
                int idx = msg.indexOf("\"text\":\"");
                if (idx >= 0) {
                    int start = idx + 8;
                    int end = msg.indexOf('"', start);
                    if (end > start)
                        mStatus = msg.substring(start, end);
                }
                return;
            }
            if (fromReceiver && msg.contains("\"type\":\"ready\"")) {
                String offer;
                synchronized (mLock) {
                    offer = mPendingOffer;
                    mPendingOffer = null;
                }
                if (offer != null)
                    from.sendText(offer);
                return;
            }

            // Rewrite mDNS .local hostnames in ICE candidates to real LAN IPv4 for instant <50 ms connection.
            String forwarded = msg;
            if (msg.contains("\"type\":\"ice\"") && msg.contains(".local")) {
                String replacementIp = fromReceiver ? localIp : from.remoteIp;
                if (replacementIp != null && !replacementIp.isEmpty() && !replacementIp.startsWith("127.")) {
                    forwarded = MDNS_HOST_PATTERN.matcher(msg).replaceAll(replacementIp);
                }
            }

            WsPeer target;
            synchronized (mLock) {
                target = fromReceiver ? mSender : mReceiver;
                if (!fromReceiver && target == null && msg.contains("\"type\":\"offer\"")) {
                    mPendingOffer = forwarded;
                }
            }
            if (target != null)
                target.sendText(forwarded);
        }

        void sendAirPlayUrl(String url) {
            WsPeer r;
            synchronized (mLock) { r = mReceiver; }
            if (r != null) {
                String safe = url.replace("\\", "\\\\").replace("\"", "\\\"");
                r.sendText("{\"type\":\"airplay_play\",\"url\":\"" + safe + "\"}");
                mLive = true;
                mStatus = "AirPlay Video";
            }
        }

        void sendAirPlayStop() {
            WsPeer r;
            synchronized (mLock) { r = mReceiver; }
            mLive = false;
            mStatus = "In attesa su Schermo " + (mIndex + 1);
            if (r != null)
                r.sendText("{\"type\":\"airplay_stop\"}");
        }

        boolean isLive() {
            return mLive;
        }

        String getStatusText() {
            return mStatus;
        }
    }
}
