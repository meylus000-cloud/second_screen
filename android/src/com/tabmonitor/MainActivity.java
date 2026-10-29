package com.tabmonitor;

import android.app.Activity;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.RectF;
import android.os.Bundle;
import android.os.Handler;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.widget.TextView;
import android.widget.Toast;

import java.io.DataInputStream;
import java.io.OutputStream;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.NetworkInterface;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.Enumeration;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Планшет = другий екран.
 *
 * Протокол (TCP, порт 7777). Планшет - сервер.
 *  1) Планшет -> ПК, одразу після з'єднання: "TBM1" + ширина(2 байти) + висота(2 байти)
 *  2) ПК -> планшет: [довжина int32 BE][JPEG]. Довжина 0 = keep-alive.
 *  3) Планшет -> ПК: 5 байт: тип(1) + x(2) + y(2), x/y у діапазоні 0..65535.
 *     тип 0 = кадр показано (ack), 1 = дотик, 2 = рух, 3 = відпускання, 4 = права кнопка.
 */
public class MainActivity extends Activity implements SurfaceHolder.Callback, View.OnTouchListener {

    static final int PORT = 7777;

    private FrameLayout root;
    private SurfaceView sv;
    private TextView info;
    private final Handler ui = new Handler();

    private volatile boolean running = true;
    private volatile boolean surfaceOk = false;
    private volatile int surfW = 0, surfH = 0;
    private volatile boolean connected = false;

    // де на екрані намальована картинка (для перерахунку дотиків)
    private volatile float dL = 0, dT = 0, dW = 0, dH = 0;

    private ServerSocket server;
    private volatile Socket client;
    private volatile OutputStream out;
    private final ExecutorService sender = Executors.newSingleThreadExecutor();

    private final Paint paint = new Paint();
    private final RectF dst = new RectF();
    private Bitmap reuse;

    private long lastBack = 0;
    private boolean ignoreMoves = false;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        root = new FrameLayout(this);
        root.setBackgroundColor(Color.BLACK);

        sv = new SurfaceView(this);
        sv.getHolder().addCallback(this);
        sv.setOnTouchListener(this);
        root.addView(sv, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        info = new TextView(this);
        info.setTextColor(Color.WHITE);
        info.setBackgroundColor(Color.BLACK);
        info.setTextSize(22);
        info.setGravity(Gravity.CENTER);
        info.setPadding(40, 40, 40, 40);
        root.addView(info, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        setContentView(root);

        root.setOnSystemUiVisibilityChangeListener(new View.OnSystemUiVisibilityChangeListener() {
            public void onSystemUiVisibilityChange(int v) {
                ui.postDelayed(new Runnable() {
                    public void run() { hideBars(); }
                }, 1500);
            }
        });
        hideBars();

        showWaiting();

        Thread t = new Thread(new Runnable() {
            public void run() { serverLoop(); }
        }, "server");
        t.setDaemon(true);
        t.start();
    }

    private void hideBars() {
        try {
            root.setSystemUiVisibility(View.SYSTEM_UI_FLAG_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_LOW_PROFILE);
        } catch (Throwable ignored) { }
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) hideBars();
    }

    // ---------- екран очікування ----------

    private final Runnable refreshInfo = new Runnable() {
        public void run() {
            if (!connected && running) {
                info.setText(buildWaitingText());
                ui.postDelayed(this, 3000);
            }
        }
    };

    private void showWaiting() {
        info.setVisibility(View.VISIBLE);
        info.setText(buildWaitingText());
        ui.removeCallbacks(refreshInfo);
        ui.postDelayed(refreshInfo, 3000);
    }

    private String buildWaitingText() {
        StringBuilder sb = new StringBuilder();
        sb.append("Другий екран\n\nОчікую підключення від ноутбука...\n\n");
        String ips = localIps();
        if (ips.length() > 0) {
            sb.append("Адреса планшета (для Wi-Fi):\n").append(ips).append("\n\n");
        } else {
            sb.append("Мережі немає. Підключіть кабель (USB-модем) або Wi-Fi.\n\n");
        }
        sb.append("Порт: ").append(PORT);
        return sb.toString();
    }

    private String localIps() {
        StringBuilder sb = new StringBuilder();
        try {
            Enumeration<NetworkInterface> en = NetworkInterface.getNetworkInterfaces();
            while (en != null && en.hasMoreElements()) {
                NetworkInterface ni = en.nextElement();
                Enumeration<InetAddress> ad = ni.getInetAddresses();
                while (ad.hasMoreElements()) {
                    InetAddress a = ad.nextElement();
                    if (a instanceof Inet4Address && !a.isLoopbackAddress()) {
                        if (sb.length() > 0) sb.append("\n");
                        sb.append(a.getHostAddress());
                    }
                }
            }
        } catch (Throwable ignored) { }
        return sb.toString();
    }

    // ---------- сервер ----------

    private void serverLoop() {
        while (running) {
            try {
                if (server == null || server.isClosed()) {
                    server = new ServerSocket();
                    server.setReuseAddress(true);
                    server.bind(new InetSocketAddress(PORT));
                }
                Socket s = server.accept();
                handle(s);
            } catch (Throwable e) {
                if (!running) break;
                try { Thread.sleep(500); } catch (InterruptedException ie) { break; }
            }
        }
    }

    private void handle(Socket s) {
        try {
            s.setTcpNoDelay(true);
            s.setKeepAlive(true);
            s.setSoTimeout(8000); // ПК шле keep-alive кожні ~1.5 с

            // чекаємо, поки з'явиться поверхня (до 3 с)
            for (int i = 0; i < 30 && surfW == 0 && running; i++) Thread.sleep(100);
            int w = surfW, h = surfH;
            if (w == 0 || h == 0) {
                android.util.DisplayMetrics dm = getResources().getDisplayMetrics();
                w = dm.widthPixels;
                h = dm.heightPixels;
            }

            OutputStream os = s.getOutputStream();
            byte[] hs = new byte[8];
            hs[0] = 'T'; hs[1] = 'B'; hs[2] = 'M'; hs[3] = '1';
            hs[4] = (byte) (w >> 8); hs[5] = (byte) w;
            hs[6] = (byte) (h >> 8); hs[7] = (byte) h;
            os.write(hs);
            os.flush();

            client = s;
            out = os;
            connected = true;
            ignoreMoves = false;
            ui.post(new Runnable() {
                public void run() { info.setVisibility(View.GONE); }
            });

            DataInputStream in = new DataInputStream(s.getInputStream());
            byte[] buf = new byte[256 * 1024];

            while (running) {
                int len = in.readInt();
                if (len == 0) continue;               // keep-alive
                if (len < 0 || len > 16 * 1024 * 1024) break;
                if (len > buf.length) buf = new byte[len + 65536];
                in.readFully(buf, 0, len);

                Bitmap bmp = decode(buf, len);
                if (bmp != null) {
                    draw(bmp);
                }
                send(0, 0, 0); // ack - можна слати наступний кадр
            }
        } catch (Throwable e) {
            // з'єднання втрачено
        } finally {
            connected = false;
            out = null;
            client = null;
            try { s.close(); } catch (Throwable ignored) { }
            reuse = null;
            ui.post(new Runnable() {
                public void run() { showWaiting(); }
            });
        }
    }

    private Bitmap decode(byte[] buf, int len) {
        Bitmap r = null;
        if (reuse != null) {
            try {
                BitmapFactory.Options o = new BitmapFactory.Options();
                o.inPreferredConfig = Bitmap.Config.RGB_565;
                o.inDither = false;
                o.inMutable = true;
                o.inBitmap = reuse;
                r = BitmapFactory.decodeByteArray(buf, 0, len, o);
            } catch (Throwable e) {
                r = null;
            }
        }
        if (r == null) {
            BitmapFactory.Options o = new BitmapFactory.Options();
            o.inPreferredConfig = Bitmap.Config.RGB_565;
            o.inDither = false;
            o.inMutable = true;
            r = BitmapFactory.decodeByteArray(buf, 0, len, o);
        }
        if (r != null) reuse = r;
        return r;
    }

    private void draw(Bitmap b) {
        if (!surfaceOk) return;
        SurfaceHolder h = sv.getHolder();
        Canvas c = null;
        try {
            c = h.lockCanvas();
            if (c == null) return;
            int cw = c.getWidth(), ch = c.getHeight();
            float s = Math.min((float) cw / b.getWidth(), (float) ch / b.getHeight());
            float w = b.getWidth() * s, hh = b.getHeight() * s;
            float l = (cw - w) / 2f, t = (ch - hh) / 2f;
            c.drawColor(Color.BLACK);
            dst.set(l, t, l + w, t + hh);
            paint.setFilterBitmap(Math.abs(s - 1f) > 0.01f);
            c.drawBitmap(b, null, dst, paint);
            dL = l; dT = t; dW = w; dH = hh;
        } catch (Throwable ignored) {
        } finally {
            if (c != null) {
                try { h.unlockCanvasAndPost(c); } catch (Throwable ignored) { }
            }
        }
    }

    // ---------- відправка на ПК ----------

    private void send(final int type, final int x, final int y) {
        final OutputStream os = out;
        if (os == null) return;
        sender.execute(new Runnable() {
            public void run() {
                try {
                    byte[] m = new byte[5];
                    m[0] = (byte) type;
                    m[1] = (byte) (x >> 8); m[2] = (byte) x;
                    m[3] = (byte) (y >> 8); m[4] = (byte) y;
                    os.write(m);
                    os.flush();
                } catch (Throwable ignored) { }
            }
        });
    }

    private int norm(float v, float origin, float size) {
        if (size <= 0) return 0;
        float n = (v - origin) / size;
        if (n < 0) n = 0;
        if (n > 1) n = 1;
        return (int) (n * 65535f);
    }

    public boolean onTouch(View v, MotionEvent e) {
        if (!connected || dW <= 0 || dH <= 0) return true;
        int a = e.getActionMasked();
        int x = norm(e.getX(0), dL, dW);
        int y = norm(e.getY(0), dT, dH);
        switch (a) {
            case MotionEvent.ACTION_DOWN:
                ignoreMoves = false;
                send(1, x, y);
                break;
            case MotionEvent.ACTION_MOVE:
                if (!ignoreMoves) send(2, x, y);
                break;
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                send(3, x, y);
                break;
            case MotionEvent.ACTION_POINTER_DOWN:
                // другий палець = права кнопка миші
                if (!ignoreMoves) {
                    ignoreMoves = true;
                    send(3, x, y);
                    send(4, x, y);
                }
                break;
        }
        return true;
    }

    // ---------- поверхня ----------

    public void surfaceCreated(SurfaceHolder holder) { surfaceOk = true; }

    public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
        surfW = width;
        surfH = height;
    }

    public void surfaceDestroyed(SurfaceHolder holder) { surfaceOk = false; }

    // ---------- вихід ----------

    @Override
    public void onBackPressed() {
        long now = System.currentTimeMillis();
        if (!connected || now - lastBack < 2000) {
            finish();
        } else {
            lastBack = now;
            Toast.makeText(this, "Натисніть «Назад» ще раз, щоб вийти", Toast.LENGTH_SHORT).show();
        }
    }

    @Override
    protected void onDestroy() {
        running = false;
        try { if (client != null) client.close(); } catch (Throwable ignored) { }
        try { if (server != null) server.close(); } catch (Throwable ignored) { }
        sender.shutdownNow();
        super.onDestroy();
    }
}
