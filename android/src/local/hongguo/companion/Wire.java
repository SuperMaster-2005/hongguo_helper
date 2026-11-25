package local.hongguo.companion;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.security.cert.X509Certificate;
import java.math.BigInteger;
import java.util.Date;
import java.util.concurrent.*;
import javax.net.ssl.*;
import javax.security.auth.x500.X500Principal;
import android.security.keystore.*;

final class Wire {
    static final int PORT = 24681, DISCOVERY = 24680, MAX_COMMAND = 8192;
    /** Socket read timeouts do not bound writes. Close a stalled peer from a separate thread. */
    static final class WriteWatchdog implements AutoCloseable {
        private final Closeable connection;
        private final long timeoutNanos;
        private final ScheduledExecutorService timer=Executors.newSingleThreadScheduledExecutor();
        private long started;
        private boolean writing,closed;
        WriteWatchdog(Closeable connection,long timeoutMillis) {
            this.connection=connection;timeoutNanos=TimeUnit.MILLISECONDS.toNanos(timeoutMillis);
            timer.scheduleWithFixedDelay(this::check,Math.min(1000,timeoutMillis),Math.min(1000,timeoutMillis),TimeUnit.MILLISECONDS);
        }
        synchronized void begin() throws IOException {
            if(closed)throw new IOException("电脑连接发送已超时或关闭。");
            started=System.nanoTime();writing=true;
        }
        synchronized void end(){writing=false;}
        private synchronized void check() {
            if(closed || !writing || System.nanoTime()-started<timeoutNanos)return;
            closed=true;timer.shutdown();
            try{connection.close();}catch(IOException ignored){}
        }
        public synchronized void close(){closed=true;writing=false;timer.shutdownNow();}
    }
    static byte[] utf8(String value) { return value.getBytes(StandardCharsets.UTF_8); }
    static void send(DataOutputStream out,int type,byte[] payload) throws IOException {
        synchronized(out) { out.writeByte(type); out.writeInt(payload.length); out.write(payload); out.flush(); }
    }
    static String readCommand(DataInputStream in) throws IOException {
        if (in.readUnsignedByte() != 16) throw new IOException("Unknown command");
        int length = in.readInt();
        if (length < 0 || length > MAX_COMMAND) throw new IOException("Command size invalid");
        byte[] value = new byte[length]; in.readFully(value); return new String(value,StandardCharsets.UTF_8);
    }
    static final class Identity {
        SSLContext context; String fingerprint;
    }
    static Identity identity() throws Exception {
        final String alias = "hongguo-local-tls-ec-v1";
        final KeyStore store = KeyStore.getInstance("AndroidKeyStore"); store.load(null);
        if (!store.containsAlias(alias)) {
            KeyPairGenerator generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC,"AndroidKeyStore");
            generator.initialize(new KeyGenParameterSpec.Builder(alias,KeyProperties.PURPOSE_SIGN | KeyProperties.PURPOSE_VERIFY)
                .setAlgorithmParameterSpec(new java.security.spec.ECGenParameterSpec("secp256r1"))
                .setDigests(KeyProperties.DIGEST_NONE, KeyProperties.DIGEST_SHA256, KeyProperties.DIGEST_SHA384, KeyProperties.DIGEST_SHA512)
                .setCertificateSubject(new X500Principal("CN=Hongguo local companion"))
                .setCertificateSerialNumber(BigInteger.ONE).setCertificateNotBefore(new Date(0))
                .setCertificateNotAfter(new Date(4102444800000L)).build());
            generator.generateKeyPair();
        }
        final X509Certificate cert = (X509Certificate)store.getCertificate(alias);
        final PrivateKey privateKey = (PrivateKey)store.getKey(alias,null);
        X509KeyManager manager = new X509KeyManager() {
            public String[] getClientAliases(String key,java.security.Principal[] issuers) { return null; }
            public String chooseClientAlias(String[] keys,java.security.Principal[] issuers,java.net.Socket socket) { return null; }
            public String[] getServerAliases(String key,java.security.Principal[] issuers) { return new String[]{alias}; }
            public String chooseServerAlias(String key,java.security.Principal[] issuers,java.net.Socket socket) { return "EC".equalsIgnoreCase(key) ? alias : null; }
            public X509Certificate[] getCertificateChain(String name) { return new X509Certificate[]{cert}; }
            public PrivateKey getPrivateKey(String name) { return privateKey; }
        };
        Identity result = new Identity(); result.context = SSLContext.getInstance("TLSv1.2");
        result.context.init(new KeyManager[]{manager},null,new SecureRandom());
        StringBuilder hash = new StringBuilder();
        for (byte b : MessageDigest.getInstance("SHA-256").digest(cert.getEncoded())) hash.append(String.format("%02x",b & 255));
        result.fingerprint = hash.toString(); return result;
    }
}
