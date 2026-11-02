export function detectPlatform() {
    const platform = `${navigator.platform ?? ''} ${navigator.userAgent ?? ''}`.toLowerCase();
    if (platform.includes('win')) return 'windows';
    if (platform.includes('mac')) return 'macos';
    return 'linux';
}
