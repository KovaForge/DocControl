import { useEffect, useState } from 'react';
import QRCode from 'qrcode';
import { AuthApi } from '../lib/api';
import { useAuth } from '../lib/authContext';
import LinkLegacyAccount from '../components/LinkLegacyAccount';

type SetupState = { secret: string; otpauthUrl: string } | null;

export default function Profile() {
  const { user, setUser } = useAuth();
  const isDev = import.meta.env.DEV;
  const authMode = isDev ? 'password' : (localStorage.getItem('dc.authMode') ?? 'microsoft');
  const [displayName, setDisplayName] = useState(user?.name ?? '');
  const [savingName, setSavingName] = useState(false);
  const [nameMessage, setNameMessage] = useState<string | null>(null);
  const [nameError, setNameError] = useState<string | null>(null);

  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [passwordMessage, setPasswordMessage] = useState<string | null>(null);
  const [passwordError, setPasswordError] = useState<string | null>(null);
  const [savingPassword, setSavingPassword] = useState(false);

  const [setup, setSetup] = useState<SetupState>(null);
  const [qrDataUrl, setQrDataUrl] = useState<string | null>(null);
  const [mfaCode, setMfaCode] = useState('');
  const [mfaMessage, setMfaMessage] = useState<string | null>(null);
  const [mfaError, setMfaError] = useState<string | null>(null);
  const [mfaLoading, setMfaLoading] = useState(false);
  const [backupCodes, setBackupCodes] = useState<string[] | null>(null);
  const [backupMessage, setBackupMessage] = useState<string | null>(null);
  const [backupError, setBackupError] = useState<string | null>(null);
  const [backupLoading, setBackupLoading] = useState(false);

  const [swaPrincipal, setSwaPrincipal] = useState<string | null>(null);

  useEffect(() => {
    setDisplayName(user?.name ?? '');
  }, [user]);

  useEffect(() => {
    if (isDev || authMode === 'password') {
      setSwaPrincipal(null);
      return;
    }
    const load = async () => {
      try {
        const res = await fetch('/.auth/me');
        if (!res.ok) return;
        const payload = await res.json();
        const details = payload?.clientPrincipal?.userDetails as string | undefined;
        if (details) {
          setSwaPrincipal(details);
        }
      } catch {
        // Ignore.
      }
    };
    void load();
  }, [authMode, isDev]);

  useEffect(() => {
    const buildQr = async () => {
      if (!setup?.otpauthUrl) {
        setQrDataUrl(null);
        return;
      }
      try {
        const dataUrl = await QRCode.toDataURL(setup.otpauthUrl, { margin: 1, width: 220 });
        setQrDataUrl(dataUrl);
      } catch {
        setQrDataUrl(null);
      }
    };
    void buildQr();
  }, [setup?.otpauthUrl]);

  const saveDisplayName = async () => {
    setNameError(null);
    setNameMessage(null);
    const trimmed = displayName.trim();
    if (!trimmed) {
      setNameError('Display name is required.');
      return;
    }
    if (!user) {
      setNameError('User not loaded yet.');
      return;
    }
    setSavingName(true);
    try {
      const result = await AuthApi.updateProfile(trimmed);
      setUser({ ...user, name: result.displayName ?? trimmed });
      setNameMessage('Display name saved.');
    } catch (err: any) {
      setNameError(err.message ?? 'Failed to save display name');
    } finally {
      setSavingName(false);
    }
  };

  const changePassword = async () => {
    setPasswordError(null);
    setPasswordMessage(null);
    if (!currentPassword.trim() || !newPassword.trim()) {
      setPasswordError('Current and new password are required.');
      return;
    }
    if (newPassword !== confirmPassword) {
      setPasswordError('New passwords do not match.');
      return;
    }
    setSavingPassword(true);
    try {
      await AuthApi.changePassword(currentPassword, newPassword);
      setPasswordMessage('Password updated.');
      setCurrentPassword('');
      setNewPassword('');
      setConfirmPassword('');
    } catch (err: any) {
      setPasswordError(err.message ?? 'Failed to change password');
    } finally {
      setSavingPassword(false);
    }
  };

  const startMfa = async () => {
    setMfaError(null);
    setMfaMessage(null);
    setMfaLoading(true);
    try {
      const result = await AuthApi.startMfa();
      setSetup(result);
      setMfaMessage(user?.mfaEnabled ? 'MFA reset started. Verify the new code to finish.' : 'MFA setup started.');
    } catch (err: any) {
      setMfaError(err.message ?? 'Failed to start MFA');
    } finally {
      setMfaLoading(false);
    }
  };

  const verifyMfa = async () => {
    if (!mfaCode.trim()) {
      setMfaError('Enter your MFA code or backup code.');
      return;
    }
    setMfaLoading(true);
    setMfaError(null);
    try {
      const result = await AuthApi.verifyMfa(mfaCode.trim());
      if (result.mfaEnabled && user) {
        setUser({ ...user, mfaEnabled: true });
        setMfaMessage('MFA enabled.');
        setSetup(null);
        setMfaCode('');
      }
    } catch (err: any) {
      setMfaError(err.message ?? 'Invalid code');
    } finally {
      setMfaLoading(false);
    }
  };

  const generateBackupCodes = async () => {
    setBackupError(null);
    setBackupMessage(null);
    if (backupCodes && backupCodes.length > 0) {
      const confirmed = window.confirm('Regenerating will invalidate your current backup codes. Continue?');
      if (!confirmed) {
        return;
      }
    }
    setBackupLoading(true);
    try {
      const result = await AuthApi.backupCodes();
      setBackupCodes(result.codes ?? []);
      setBackupMessage(backupCodes ? 'Backup codes regenerated. Previous codes are no longer valid.' : 'Backup codes generated. Store them somewhere safe.');
    } catch (err: any) {
      setBackupError(err.message ?? 'Failed to generate backup codes');
    } finally {
      setBackupLoading(false);
    }
  };

  const copyBackupCodes = async () => {
    if (!backupCodes || backupCodes.length === 0) return;
    const payload = backupCodes.join('\n');
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(payload);
        setBackupMessage('Backup codes copied to clipboard.');
        return;
      }
      setBackupError('Clipboard access is not available in this browser.');
    } catch (err: any) {
      setBackupError(err.message ?? 'Failed to copy backup codes');
    }
  };

  const saveBackupCodes = () => {
    if (!backupCodes || backupCodes.length === 0) return;
    const blob = new Blob([`${backupCodes.join('\n')}\n`], { type: 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = 'doccontrol-backup-codes.txt';
    anchor.click();
    URL.revokeObjectURL(url);
    setBackupMessage('Backup codes saved as a text file.');
  };

  return (
    <div className="page">
      <h1>Profile</h1>
      <p className="muted">Manage your personal settings, password, and MFA.</p>

      <div className="card" style={{ marginTop: 12 }}>
        <h3>Display name</h3>
        {nameMessage && <div className="pill" style={{ background: '#ecfdf3', color: '#166534' }}>{nameMessage}</div>}
        {nameError && <div className="pill" style={{ background: '#fee2e2', color: '#991b1b' }}>{nameError}</div>}
        <div className="stack">
          <label>Display name</label>
          <input value={displayName} onChange={(e) => setDisplayName(e.target.value)} placeholder="Your name" />
          <button type="button" onClick={saveDisplayName} disabled={savingName || !displayName.trim()}>
            {savingName ? 'Saving...' : 'Save display name'}
          </button>
        </div>
      </div>

      <div className="card" style={{ marginTop: 12 }}>
        <h3>Change password</h3>
        {authMode !== 'password' && (
          <p className="muted">Password changes are available for email + MFA sign-in.</p>
        )}
        {passwordMessage && <div className="pill" style={{ background: '#ecfdf3', color: '#166534' }}>{passwordMessage}</div>}
        {passwordError && <div className="pill" style={{ background: '#fee2e2', color: '#991b1b' }}>{passwordError}</div>}
        <div className="stack">
          <label>Current password</label>
          <input value={currentPassword} onChange={(e) => setCurrentPassword(e.target.value)} type="password" />
          <label>New password</label>
          <input value={newPassword} onChange={(e) => setNewPassword(e.target.value)} type="password" />
          <label>Confirm new password</label>
          <input value={confirmPassword} onChange={(e) => setConfirmPassword(e.target.value)} type="password" />
          <button type="button" onClick={changePassword} disabled={savingPassword || authMode !== 'password'}>
            {savingPassword ? 'Updating...' : 'Update password'}
          </button>
        </div>
      </div>

      <div className="card" style={{ marginTop: 12 }}>
        <h3>Two-factor authentication</h3>
        {authMode !== 'password' && (
          <p className="muted">MFA setup is available for email + MFA sign-in.</p>
        )}
        {mfaMessage && <div className="pill" style={{ background: '#ecfdf3', color: '#166534' }}>{mfaMessage}</div>}
        {mfaError && <div className="pill" style={{ background: '#fee2e2', color: '#991b1b' }}>{mfaError}</div>}
        {backupMessage && <div className="pill" style={{ background: '#ecfdf3', color: '#166534' }}>{backupMessage}</div>}
        {backupError && <div className="pill" style={{ background: '#fee2e2', color: '#991b1b' }}>{backupError}</div>}
        <div className="stack">
          <button type="button" onClick={startMfa} disabled={mfaLoading || authMode !== 'password'}>
            {mfaLoading ? 'Working...' : user?.mfaEnabled ? 'Reset MFA' : 'Enable MFA'}
          </button>
          {setup?.secret && (
            <>
              <label>Secret</label>
              <input value={setup.secret} readOnly />
            </>
          )}
          {setup?.otpauthUrl && (
            <div style={{ marginTop: 8 }}>
              <strong>Scan QR</strong>
              <div style={{ marginTop: 8 }}>
                {qrDataUrl ? (
                  <img
                    src={qrDataUrl}
                    alt="MFA QR code"
                    style={{ background: '#fff', padding: 8, borderRadius: 8 }}
                  />
                ) : (
                  <div className="muted">QR code unavailable.</div>
                )}
              </div>
            </div>
          )}
          {setup && (
            <>
              <label>MFA or backup code</label>
              <input value={mfaCode} onChange={(e) => setMfaCode(e.target.value)} placeholder="123456 or ABCDE-FGHIJ" inputMode="text" />
              <button type="button" onClick={verifyMfa} disabled={mfaLoading || authMode !== 'password'}>
                {mfaLoading ? 'Verifying...' : 'Verify'}
              </button>
            </>
          )}
          <section aria-labelledby="backup-codes-heading">
            <h4 id="backup-codes-heading" style={{ margin: 0 }}>Backup codes</h4>
            <p className="muted" style={{ margin: '6px 0 0' }}>
              Generate one-time backup codes in case you lose access to your authenticator.
            </p>
          </section>
          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
            <button type="button" onClick={generateBackupCodes} disabled={backupLoading || authMode !== 'password' || !user?.mfaEnabled}>
              {backupLoading ? 'Generating...' : backupCodes ? 'Regenerate codes' : 'Generate codes'}
            </button>
            {(authMode !== 'password' || !user?.mfaEnabled) && (
              <span className="muted" style={{ alignSelf: 'center', fontSize: 13 }}>Available for email + MFA sign-in only.</span>
            )}
            <button type="button" onClick={copyBackupCodes} disabled={!backupCodes || backupCodes.length === 0}>
              Copy to clipboard
            </button>
            <button type="button" onClick={saveBackupCodes} disabled={!backupCodes || backupCodes.length === 0}>
              Save as .txt
            </button>
          </div>
          {backupCodes && backupCodes.length > 0 && (
            <div className="card" style={{ background: '#0b1021' }}>
              <p className="muted" style={{ marginTop: 0 }}>Codes are shown once. Store them securely.</p>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))', gap: 8 }}>
                {backupCodes.map((code) => (
                  <div key={code} className="pill" style={{ justifyContent: 'center' }}>{code}</div>
                ))}
              </div>
            </div>
          )}
        </div>
      </div>

      <div className="card" style={{ marginTop: 12 }}>
        <h3>Microsoft account</h3>
        {authMode === 'password' && (
          <p className="muted">Sign in with Microsoft to link it to your account.</p>
        )}
        {swaPrincipal ? (
          <p className="muted">Connected as {swaPrincipal}.</p>
        ) : (
          <p className="muted">No Microsoft account connected.</p>
        )}
        <div className="stack">
          <p className="muted" style={{ margin: 0 }}>Re-authenticate with Microsoft if your session expires.</p>
          <a
            className="button"
            href="/.auth/login/aad?post_login_redirect_uri=/profile"
            style={{ textAlign: 'center', textDecoration: 'none' }}
          >
            Sign in with Microsoft
          </a>
        </div>
        {!isDev && (
          <div style={{ marginTop: 12 }}>
            <LinkLegacyAccount />
          </div>
        )}
      </div>
    </div>
  );
}
