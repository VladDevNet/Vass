const fs = require('fs');
const path = require('path');
const { AndroidConfig, withAndroidManifest, withDangerousMod } = require('expo/config-plugins');

const SERVICE_NAME = 'com.vass.overlay.VassOverlayService';
const SCREEN_CAPTURE_SERVICE = 'com.vass.overlay.VassMediaProjectionService';
const SCREEN_CAPTURE_ACTIVITY = 'com.vass.overlay.ScreenCapturePermissionActivity';
const SPECIAL_USE_PROPERTY = 'android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE';

function addPermission(manifest, name) {
  manifest['uses-permission'] = manifest['uses-permission'] ?? [];
  const permissions = manifest['uses-permission'];
  if (!permissions.some((item) => item.$?.['android:name'] === name)) {
    AndroidConfig.Permissions.addPermissionToManifest(name, permissions);
  }
}

function withOverlayManifest(config) {
  return withAndroidManifest(config, (mod) => {
    const manifest = mod.modResults.manifest;
    addPermission(manifest, 'android.permission.SYSTEM_ALERT_WINDOW');
    addPermission(manifest, 'android.permission.FOREGROUND_SERVICE');
    addPermission(manifest, 'android.permission.FOREGROUND_SERVICE_SPECIAL_USE');
    addPermission(manifest, 'android.permission.FOREGROUND_SERVICE_MEDIA_PROJECTION');

    const application = manifest.application?.[0];
    if (!application) throw new Error('AndroidManifest is missing the application element');

    application.service = application.service ?? [];
    const existing = application.service.find((item) => item.$?.['android:name'] === SERVICE_NAME);
    const service = existing ?? { $: {} };
    service.$ = {
      ...service.$,
      'android:name': SERVICE_NAME,
      'android:exported': 'false',
      'android:foregroundServiceType': 'specialUse',
      'android:stopWithTask': 'false',
    };
    service.property = [
      {
        $: {
          'android:name': SPECIAL_USE_PROPERTY,
          'android:value': 'User-enabled interactive voice assistant control displayed over other apps',
        },
      },
    ];
    if (!existing) application.service.push(service);

    const captureService = application.service.find((item) => item.$?.['android:name'] === SCREEN_CAPTURE_SERVICE) ?? { $: {} };
    captureService.$ = {
      ...captureService.$,
      'android:name': SCREEN_CAPTURE_SERVICE,
      'android:exported': 'false',
      'android:foregroundServiceType': 'mediaProjection',
      'android:stopWithTask': 'false',
    };
    if (!application.service.includes(captureService)) application.service.push(captureService);

    application.activity = application.activity ?? [];
    const mainActivity = application.activity.find((item) => {
      const name = item.$?.['android:name'];
      return name === '.MainActivity' || name?.endsWith('.MainActivity');
    });
    if (!mainActivity) throw new Error('AndroidManifest is missing MainActivity');
    mainActivity['intent-filter'] = mainActivity['intent-filter'] ?? [];
    const hasImageShareFilter = mainActivity['intent-filter'].some((filter) =>
      filter.action?.some((action) => action.$?.['android:name'] === 'android.intent.action.SEND') &&
      filter.data?.some((data) => data.$?.['android:mimeType'] === 'image/*')
    );
    if (!hasImageShareFilter) {
      mainActivity['intent-filter'].push({
        action: [{ $: { 'android:name': 'android.intent.action.SEND' } }],
        category: [{ $: { 'android:name': 'android.intent.category.DEFAULT' } }],
        data: [{ $: { 'android:mimeType': 'image/*' } }],
      });
    }

    const captureActivity = application.activity.find((item) => item.$?.['android:name'] === SCREEN_CAPTURE_ACTIVITY) ?? { $: {} };
    captureActivity.$ = {
      ...captureActivity.$,
      'android:name': SCREEN_CAPTURE_ACTIVITY,
      'android:exported': 'false',
      'android:excludeFromRecents': 'true',
      'android:theme': '@style/AppTheme',
    };
    if (!application.activity.includes(captureActivity)) application.activity.push(captureActivity);
    return mod;
  });
}

function withOverlayAvatarAssets(config) {
  return withDangerousMod(config, [
    'android',
    async (mod) => {
      const drawableDir = path.join(mod.modRequest.platformProjectRoot, 'app', 'src', 'main', 'res', 'drawable-nodpi');
      fs.mkdirSync(drawableDir, { recursive: true });
      for (const avatar of ['olga', 'male']) {
        const source = path.join(mod.modRequest.projectRoot, 'assets', 'avatar', `${avatar}_base.png`);
        const destination = path.join(drawableDir, `vass_overlay_${avatar}.png`);
        fs.copyFileSync(source, destination);
      }
      return mod;
    },
  ]);
}

module.exports = function withVassOverlay(config) {
  return withOverlayAvatarAssets(withOverlayManifest(config));
};
