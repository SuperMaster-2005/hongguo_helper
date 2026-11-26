package local.hongguo.companion;

import android.Manifest;
import android.app.*;
import android.os.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.content.res.ColorStateList;
import android.graphics.*;
import android.graphics.drawable.*;
import android.media.projection.*;
import android.provider.Settings;
import android.view.*;
import android.widget.*;

public class MainActivity extends Activity {
    private static final int BG=0xff0e0f14, CARD=0xff1b1d27, LINE=0xff2a2c38, TEXT=0xfff2f3f6, HINT=0xff8b90a2,
        RED=0xffed4055, RED_DARK=0xffc92e45, GREEN=0xff3dd68c, AMBER=0xfff5a623, BLUE=0xffd5e0ff, BADGE=0xff33364a;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private TextView status, control;
    private View controlDot;
    private Button start, stop;
    private CheckBox audio;
    private CaptureService.Session shown;
    private AlertDialog approval;
    private static final int ACCESSIBILITY_FOR_SHARE = 22;
    private static final long CONTROL_SERVICE_WAIT_MS = 3500;
    private long waitForControlUntil;
    private final Runnable update = new Runnable() { public void run() {
        status.setText(CaptureService.status);
        boolean on = RemoteAccessibility.instance != null;
        if (waitForControlUntil != 0) {
            if (on) {
                waitForControlUntil = 0;
                continueSharing();
            } else if (SystemClock.elapsedRealtime() >= waitForControlUntil) {
                waitForControlUntil = 0;
                CaptureService.status = "电脑控制服务尚未连接，可继续等待或仅观看。";
                status.setText(CaptureService.status);
                new AlertDialog.Builder(MainActivity.this).setTitle("电脑控制尚未就绪")
                    .setMessage("部分系统在开启权限后需要时间连接服务。可继续等待；仅观看不需要此权限。若一直未就绪，请在系统设置检查无障碍权限和应用运行限制。")
                    .setPositiveButton("继续等待",(d,w)->waitForControlUntil=SystemClock.elapsedRealtime()+CONTROL_SERVICE_WAIT_MS)
                    .setNegativeButton("仅观看",(d,w)->watchOnly()).show();
            }
        }
        control.setText(on ? "电脑控制已开启，可识别红果前台" : "电脑控制未开启，仍可观看");
        controlDot.setBackground(dot(on ? GREEN : AMBER));
        start.setEnabled(!CaptureService.running);
        stop.setEnabled(CaptureService.running);
        start.setAlpha(CaptureService.running ? 0.4f : 1f);
        stop.setAlpha(CaptureService.running ? 1f : 0.4f);
        CaptureService.Session pending = CaptureService.pending;
        if (approval != null && pending != shown) { approval.dismiss(); approval = null; }
        if (pending != null && pending != shown) {
            shown = pending;
            approval = new AlertDialog.Builder(MainActivity.this).setTitle("允许这台电脑观看并控制？")
                .setMessage("电脑：" + pending.name + "\n局域网地址：" + pending.address + "\n\n仅在这是你刚操作的电脑时允许。连接期间电脑可看到共享画面，开启控制后可操作手机。")
                .setPositiveButton("允许本次", (d,w) -> pending.decide(true))
                .setNegativeButton("拒绝", (d,w) -> pending.decide(false))
                .setOnCancelListener(d -> pending.decide(false)).show();
        }
        handler.postDelayed(this, 700);
    }};

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().setStatusBarColor(BG);
        getWindow().setNavigationBarColor(BG);
        ScrollView scroll = new ScrollView(this); scroll.setBackgroundColor(BG);
        LinearLayout body = new LinearLayout(this); body.setOrientation(LinearLayout.VERTICAL);
        body.setPadding(dp(20),dp(40),dp(20),dp(24)); scroll.addView(body); setContentView(scroll);
        scroll.setOnApplyWindowInsetsListener((view,insets) -> {
            if (Build.VERSION.SDK_INT >= 30) {
                Insets safe = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                body.setPadding(safe.left+dp(20),safe.top+dp(20),safe.right+dp(20),safe.bottom+dp(20));
            } else {
                body.setPadding(insets.getSystemWindowInsetLeft()+dp(20),insets.getSystemWindowInsetTop()+dp(20),
                    insets.getSystemWindowInsetRight()+dp(20),insets.getSystemWindowInsetBottom()+dp(20));
            }
            return insets;
        });

        LinearLayout header = new LinearLayout(this); header.setOrientation(LinearLayout.HORIZONTAL); header.setGravity(Gravity.CENTER_VERTICAL);
        TextView logo = new TextView(this); logo.setText("▶"); logo.setTextSize(18); logo.setTextColor(Color.WHITE); logo.setGravity(Gravity.CENTER);
        GradientDrawable logoBg = round(RED, 13); logoBg.setColors(new int[]{0xfff2546c, 0xffc92647}); logoBg.setOrientation(GradientDrawable.Orientation.TL_BR);
        logo.setBackground(logoBg);
        int logoSize = dp(46); LinearLayout.LayoutParams logoParams = new LinearLayout.LayoutParams(logoSize, logoSize); logoParams.rightMargin = dp(14);
        header.addView(logo, logoParams);
        LinearLayout titles = new LinearLayout(this); titles.setOrientation(LinearLayout.VERTICAL);
        TextView title = new TextView(this); title.setText("红果手机助手"); title.setTextSize(23); title.setTextColor(TEXT); title.setTypeface(Typeface.DEFAULT_BOLD);
        TextView subtitle = new TextView(this); subtitle.setText("连上热点，大屏接着看"); subtitle.setTextSize(13); subtitle.setTextColor(HINT); subtitle.setPadding(0, dp(2), 0, 0);
        titles.addView(title); titles.addView(subtitle); header.addView(titles);
        LinearLayout.LayoutParams headerParams = new LinearLayout.LayoutParams(-1, -2); headerParams.bottomMargin = dp(24);
        body.addView(header, headerParams);

        LinearLayout steps = card(body);
        sectionLabel(steps, "使用步骤");
        step(steps, 1, "电脑连接这台手机的热点");
        step(steps, 2, "按需开启电脑控制，仅观看可跳过");
        step(steps, 3, "点击开始共享，确认系统提示");
        step(steps, 4, "电脑自动查找，这里允许连接");
        step(steps, 5, "打开红果短剧，电脑自动显示画面");

        LinearLayout controlCard = card(body);
        LinearLayout controlRow = new LinearLayout(this); controlRow.setOrientation(LinearLayout.HORIZONTAL); controlRow.setGravity(Gravity.CENTER_VERTICAL);
        controlDot = new View(this); controlDot.setBackground(dot(AMBER));
        int dotSize = dp(10); LinearLayout.LayoutParams dotParams = new LinearLayout.LayoutParams(dotSize, dotSize); dotParams.rightMargin = dp(10);
        controlRow.addView(controlDot, dotParams);
        control = new TextView(this); control.setTextSize(14); control.setTextColor(HINT);
        controlRow.addView(control, new LinearLayout.LayoutParams(0, -2, 1));
        controlCard.addView(controlRow);
        Button enable = button(controlCard, "开启电脑控制", false); ((LinearLayout.LayoutParams)enable.getLayoutParams()).topMargin = dp(12);
        enable.setOnClickListener(v -> {
            getPreferences(MODE_PRIVATE).edit().putBoolean("watchOnly",false).apply();
            if (RemoteAccessibility.instance != null) beginSharing();
            else openControlSettings(true);
        });

        LinearLayout shareCard = card(body);
        audio = new CheckBox(this); audio.setText("声音传到电脑，并将手机媒体音量设为零"); audio.setTextColor(TEXT); audio.setTextSize(15);
        audio.setChecked(state==null || state.getBoolean("audio",true));
        if(state!=null)waitForControlUntil=state.getLong("waitForControlUntil",0);
        audio.setButtonTintList(new ColorStateList(new int[][]{{android.R.attr.state_checked},{}}, new int[]{RED, 0xff5b6070}));
        audio.setPadding(0, 0, 0, dp(10)); shareCard.addView(audio);
        start = button(shareCard, "开始共享", true);
        start.setOnClickListener(v -> beginSharing());
        stop = button(shareCard, "停止共享", false);
        stop.setOnClickListener(v -> stopService(new Intent(this,CaptureService.class)));

        LinearLayout statusCard = card(body);
        sectionLabel(statusCard, "当前状态");
        status = new TextView(this); status.setText(CaptureService.status); status.setTextSize(15); status.setTextColor(BLUE); status.setLineSpacing(0, 1.2f);
        statusCard.addView(status);

        TextView note = new TextView(this); note.setTextSize(13); note.setTextColor(HINT); note.setLineSpacing(0, 1.35f);
        note.setPadding(dp(4), dp(4), dp(4), 0);
        note.setText("不需要 USB 或无线调试，不填写 IP。共享期间保留通知，随时可停止；电脑连接需在这里允许。需要安卓 14 及以上；请在系统弹窗选择「单个应用」并选择红果短剧，不要选择整个屏幕。系统未提供所选应用身份核验接口，请务必选对红果。\n\n视频权限保护可能导致黑屏；若电脑无声，请停止共享并关闭声音转发后重试。断开连接会恢复手机原媒体音量（手动改过音量则保留）。优先使用 60 帧硬件编码，并尽量保留原画尺寸，实际播放帧率可在电脑查看。");
        body.addView(note);

        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},21);
    }
    private void openControlSettings(boolean continueSharing) {
        // Android exposes this route on some systems but not as a public SDK constant.
        Intent details = new Intent("android.settings.ACCESSIBILITY_DETAILS_SETTINGS")
            .putExtra(Intent.EXTRA_COMPONENT_NAME,new ComponentName(this,RemoteAccessibility.class).flattenToString());
        try {
            if (continueSharing) startActivityForResult(details, ACCESSIBILITY_FOR_SHARE);
            else startActivity(details);
            return;
        } catch (ActivityNotFoundException | SecurityException unavailable) {
            // Some vendors expose only the accessibility service list.
        }
        Intent settings = new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS);
        try {
            if (continueSharing) startActivityForResult(settings, ACCESSIBILITY_FOR_SHARE);
            else startActivity(settings);
        } catch (ActivityNotFoundException | SecurityException e) {
            new AlertDialog.Builder(this).setTitle("请手动开启电脑控制")
                .setMessage("此系统无法从助手打开无障碍设置。请在系统设置搜索「无障碍」，启用「红果手机助手 · 电脑控制」，再返回开始共享。")
                .setPositiveButton("打开系统设置",(d,w)->{
                    try {
                        Intent general=new Intent(Settings.ACTION_SETTINGS);
                        if(continueSharing)startActivityForResult(general,ACCESSIBILITY_FOR_SHARE);else startActivity(general);
                    }catch(ActivityNotFoundException | SecurityException unavailable){
                        CaptureService.status="无法打开系统设置，请手动进入设置开启电脑控制，或选择仅观看。";
                        status.setText(CaptureService.status);
                    }
                }).setNegativeButton("取消",null).show();
        }
    }
    private void beginSharing() {
        if (CaptureService.running || waitForControlUntil != 0) return;
        if(Build.VERSION.SDK_INT<34){
            new AlertDialog.Builder(this).setTitle("当前系统不支持单应用共享")
                .setMessage("仅红果共享需要安卓 14 及以上。此设备不启动整屏采集，避免共享其他应用。")
                .setPositiveButton("知道了",null).show();return;
        }
        if (RemoteAccessibility.instance == null && !getPreferences(MODE_PRIVATE).getBoolean("watchOnly",false)) {
            new AlertDialog.Builder(this).setTitle("电脑控制可选")
                .setMessage("不启用电脑控制也可以观看。若需要从电脑点击、滑动或输入文字，可先开启权限。")
                .setPositiveButton("去开启", (d,w) -> {
                    getPreferences(MODE_PRIVATE).edit().putBoolean("watchOnly",false).apply();
                    openControlSettings(true);
                })
                .setNegativeButton("仅观看", (d,w) -> watchOnly()).show();
            return;
        }
        continueSharing();
    }
    private void watchOnly() {
        getPreferences(MODE_PRIVATE).edit().putBoolean("watchOnly",true).apply();
        continueSharing();
    }
    private void continueSharing() {
        if (CaptureService.running) return;
        if (audio.isChecked() && checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            new AlertDialog.Builder(this).setTitle("转发应用声音")
                .setMessage("安卓将应用音频捕获归入录音权限。本应用仅捕获应用播放声音，不采集麦克风。拒绝后仍可共享画面。")
                .setPositiveButton("继续",(d,w)->requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO},20))
                .setNegativeButton("只共享画面",(d,w)->{audio.setChecked(false); requestCapture();}).show();
        } else requestCapture();
    }
    private void requestCapture() {
        if (CaptureService.running) return;
        try {
            MediaProjectionManager manager = (MediaProjectionManager)getSystemService(MEDIA_PROJECTION_SERVICE);
            if(manager==null)throw new IllegalStateException("屏幕捕获服务不可用");
            Intent intent = manager.createScreenCaptureIntent();
            startActivityForResult(intent,10);
        }catch(RuntimeException e){
            android.util.Log.w("HongguoActivity","无法请求共享授权",e);
            CaptureService.status="系统无法打开屏幕共享授权。请检查系统版本、应用运行限制，或更新手机系统后重试。";
            status.setText(CaptureService.status);
        }
    }
    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] grants) {
        super.onRequestPermissionsResult(request,permissions,grants);
        if (request == 20) { audio.setChecked(grants.length > 0 && grants[0] == PackageManager.PERMISSION_GRANTED); requestCapture(); }
        if (request == 21 && (grants.length==0 || grants[0]!=PackageManager.PERMISSION_GRANTED)) {
            CaptureService.status="通知权限未开启。共享期间可回到助手点「停止共享」，也可使用系统的屏幕共享提示停止。";
            status.setText(CaptureService.status);
        }
    }
    @Override protected void onActivityResult(int request,int result,Intent data) {
        super.onActivityResult(request,result,data);
        if (request == ACCESSIBILITY_FOR_SHARE) {
            // Settings does not return a grant result; allow time for Android to bind the service.
            waitForControlUntil = SystemClock.elapsedRealtime() + CONTROL_SERVICE_WAIT_MS;
            return;
        }
        if (request == 10 && result == RESULT_OK && data != null) {
            if (CaptureService.running) return;
            Intent service = new Intent(this,CaptureService.class).putExtra("capture",data)
                .putExtra("audio",audio.isChecked());
            try { startForegroundService(service); }
            catch(RuntimeException e){
                android.util.Log.w("HongguoActivity","无法启动共享服务",e);
                CaptureService.status="系统未允许启动共享。请保持助手在前台重新授权，并检查应用运行限制。";
                status.setText(CaptureService.status);
            }
        } else if (request == 10) status.setText("你取消了共享。需要时可再次点击开始共享。");
    }
    @Override protected void onResume() { super.onResume(); handler.post(update); }
    @Override protected void onSaveInstanceState(Bundle state) {
        state.putBoolean("audio",audio.isChecked());state.putLong("waitForControlUntil",waitForControlUntil);
        super.onSaveInstanceState(state);
    }
    @Override protected void onPause() { handler.removeCallbacks(update); super.onPause(); }
    @Override protected void onDestroy() { handler.removeCallbacks(update); if (approval != null) approval.dismiss(); super.onDestroy(); }

    private LinearLayout card(LinearLayout body) {
        LinearLayout card = new LinearLayout(this); card.setOrientation(LinearLayout.VERTICAL);
        GradientDrawable bg = round(CARD, 18); bg.setStroke(dp(1), LINE); card.setBackground(bg);
        card.setPadding(dp(18), dp(16), dp(18), dp(18));
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2); params.bottomMargin = dp(16);
        body.addView(card, params); return card;
    }
    private void sectionLabel(LinearLayout parent, String text) {
        TextView view = new TextView(this); view.setText(text); view.setTextSize(13); view.setTextColor(HINT); view.setTypeface(Typeface.DEFAULT_BOLD);
        view.setPadding(0, 0, 0, dp(12)); parent.addView(view);
    }
    private void step(LinearLayout parent, int number, String text) {
        LinearLayout row = new LinearLayout(this); row.setOrientation(LinearLayout.HORIZONTAL); row.setGravity(Gravity.CENTER_VERTICAL);
        TextView badge = new TextView(this); badge.setText(String.valueOf(number)); badge.setTextColor(Color.WHITE); badge.setTextSize(12); badge.setGravity(Gravity.CENTER);
        badge.setBackground(round(BADGE, 12));
        int size = dp(24); LinearLayout.LayoutParams badgeParams = new LinearLayout.LayoutParams(size, size); badgeParams.rightMargin = dp(12);
        row.addView(badge, badgeParams);
        TextView label = new TextView(this); label.setText(text); label.setTextSize(15); label.setTextColor(TEXT); label.setLineSpacing(0, 1.2f);
        row.addView(label, new LinearLayout.LayoutParams(0, -2, 1));
        LinearLayout.LayoutParams rowParams = new LinearLayout.LayoutParams(-1, -2); rowParams.topMargin = dp(9);
        parent.addView(row, rowParams);
    }
    private Button button(LinearLayout parent, String text, boolean primary) {
        Button b = new Button(this); b.setText(text); b.setAllCaps(false); b.setTextSize(16); b.setTypeface(Typeface.DEFAULT_BOLD);
        StateListDrawable bg = new StateListDrawable();
        if (primary) {
            b.setTextColor(Color.WHITE);
            bg.addState(new int[]{-android.R.attr.state_enabled}, round((RED & 0xffffff) | 0x55000000, 14));
            bg.addState(new int[]{android.R.attr.state_pressed}, round(RED_DARK, 14));
            bg.addState(new int[]{}, round(RED, 14));
        } else {
            b.setTextColor(0xffe8eaf2);
            GradientDrawable normal = round(CARD, 14); normal.setStroke(dp(1), 0xff3a3d4d);
            GradientDrawable pressed = round(0xff262935, 14); pressed.setStroke(dp(1), 0xff474b5e);
            GradientDrawable disabled = round(CARD, 14); disabled.setStroke(dp(1), 0xff2a2c38);
            bg.addState(new int[]{-android.R.attr.state_enabled}, disabled);
            bg.addState(new int[]{android.R.attr.state_pressed}, pressed);
            bg.addState(new int[]{}, normal);
        }
        b.setBackground(bg);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, dp(56)); params.topMargin = dp(8);
        parent.addView(b, params); return b;
    }
    private GradientDrawable round(int color, int radius) {
        GradientDrawable d = new GradientDrawable(); d.setColor(color); d.setCornerRadius(dp(radius)); return d;
    }
    private GradientDrawable dot(int color) {
        GradientDrawable d = new GradientDrawable(); d.setShape(GradientDrawable.OVAL); d.setColor(color); return d;
    }
    private int dp(int value) { return Math.round(value*getResources().getDisplayMetrics().density); }
}
