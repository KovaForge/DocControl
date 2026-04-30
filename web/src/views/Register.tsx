import { type FormEvent, useEffect, useRef, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { AuthApi } from '../lib/api';
import { useAuth } from '../lib/authContext';
import authBackground from '../assets/background.png';

export default function Register() {
  const { user, setUser, clearUser } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const isDev = import.meta.env.DEV;
  const [authMode, setAuthMode] = useState<'microsoft' | 'password'>(() => {
    if (isDev) return 'password';
    const stored = localStorage.getItem('dc.authMode');
    return stored === 'password' ? 'password' : 'microsoft';
  });
  const [mode, setMode] = useState<'register' | 'login' | 'setPassword'>('login');
  const [emailInput, setEmailInput] = useState<HTMLInputElement | null>(null);
  const [mfaInput, setMfaInput] = useState<HTMLInputElement | null>(null);
  const [email, setEmail] = useState(user?.email ?? '');
  const [name, setName] = useState(user?.name ?? '');
  const [password, setPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [mfaCode, setMfaCode] = useState('');
  const [loading, setLoading] = useState(false);
  const [checkingAuth, setCheckingAuth] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [skipRedirect, setSkipRedirect] = useState(false);
  const authCheckStarted = useRef(false);

  useEffect(() => {
    if (user && !skipRedirect) {
      navigate(user.mfaEnabled ? '/projects' : '/mfa', { replace: true });
    }
  }, [user, navigate, skipRedirect]);

  useEffect(() => {
    if (isDev || authMode === 'password') return;
    if (authCheckStarted.current || user) return;
    authCheckStarted.current = true;
    const check = async () => {
      setCheckingAuth(true);
      try {
        const res = await fetch('/.auth/me');
        if (!res.ok) {
          return;
        }
        const payload = await res.json();
        if (!payload?.clientPrincipal?.userDetails) {
          return;
        }
        const me = await AuthApi.me();
        setUser({ id: me.userId, email: me.email, name: me.displayName, mfaEnabled: true, needsLink: !me.hasPassword });
        navigate('/', { replace: true });
      } catch {
        // Ignore and show sign-in prompt.
      } finally {
        setCheckingAuth(false);
      }
    };
    void check();
  }, [authMode, isDev, navigate, setUser, user]);

  const setMicrosoftMode = () => {
    localStorage.removeItem('dc.authToken');
    localStorage.removeItem('dc.userId');
    localStorage.removeItem('dc.email');
    localStorage.removeItem('dc.name');
    localStorage.removeItem('dc.mfa');
    localStorage.setItem('dc.authMode', 'microsoft');
    setAuthMode('microsoft');
  };

  const setPasswordMode = () => {
    localStorage.setItem('dc.authMode', 'password');
    setAuthMode('password');
  };

  const storePasswordSession = (token: string | undefined, userId: number, userEmail: string, displayName: string, mfaEnabled: boolean) => {
    if (isDev) return;
    if (token) {
      localStorage.setItem('dc.authToken', token);
    }
    localStorage.setItem('dc.authMode', 'password');
    localStorage.setItem('dc.userId', userId.toString());
    localStorage.setItem('dc.email', userEmail);
    localStorage.setItem('dc.name', displayName);
    localStorage.setItem('dc.mfa', mfaEnabled ? 'true' : 'false');
  };

  const authBackgroundStyle = { backgroundImage: `url(${authBackground})` };

  if (!isDev && authMode === 'microsoft') {
    return (
      <div className="auth-screen" style={authBackgroundStyle}>
        <div className="page auth-panel">
          <h1>Sign in</h1>
          <p className="muted">Use your Microsoft account to access DocControl, or sign in with email + MFA.</p>
          <div className="card stack" style={{ marginTop: 16 }}>
            {checkingAuth && <div className="muted">Checking sign-in status...</div>}
            <a
              className="button"
              href="/.auth/login/aad?post_login_redirect_uri=/"
              style={{ textAlign: 'center', textDecoration: 'none' }}
              onClick={setMicrosoftMode}
            >
              Sign in with Microsoft
            </a>
            <button type="button" onClick={setPasswordMode} style={{ background: '#334155', color: '#e2e8f0' }}>
              Use email + MFA
            </button>
          </div>
        </div>
      </div>
    );
  }

  const switchMode = (next: 'register' | 'login' | 'setPassword') => {
    setMode(next);
    setError(null);
    setLoading(false);
    setMfaCode('');
    setPassword('');
    setConfirmPassword('');
    if (next === 'login') {
      setName('');
      emailInput?.focus();
    } else if (next === 'register') {
      mfaInput?.blur();
    }
  };

  const registerUser = async (targetOverride?: string) => {
    if (!email.trim()) {
      setError('Email is required');
      emailInput?.focus();
      return;
    }
    if (!password.trim()) {
      setError('Password is required');
      return;
    }
    if (password !== confirmPassword) {
      setError('Passwords do not match');
      return;
    }

    setLoading(true);
    setError(null);
    try {
      const registered = await AuthApi.register(email.trim(), name.trim(), password);
      storePasswordSession(
        registered.authToken,
        registered.id,
        registered.email,
        registered.displayName ?? registered.email,
        registered.mfaEnabled
      );
      setUser({
        id: registered.id,
        email: registered.email,
        name: registered.displayName ?? registered.email,
        mfaEnabled: registered.mfaEnabled,
      });
      const target = targetOverride ?? (location.state as { from?: string } | null)?.from ?? '/mfa';
      navigate(target, { replace: true });
    } catch (err: any) {
      setError(err.message ?? 'Failed to register');
    } finally {
      setLoading(false);
    }
  };

  const loginUser = async () => {
    if (!email.trim()) {
      setError('Email is required');
      emailInput?.focus();
      return;
    }
    if (!password.trim()) {
      setError('Password is required');
      return;
    }

    setLoading(true);
    setError(null);
    setSkipRedirect(true);
    try {
      const registered = await AuthApi.login(email.trim(), password);
      storePasswordSession(
        registered.authToken,
        registered.id,
        registered.email,
        registered.displayName ?? registered.email,
        registered.mfaEnabled
      );
      setUser({
        id: registered.id,
        email: registered.email,
        name: registered.displayName ?? registered.email,
        mfaEnabled: registered.mfaEnabled,
      });
      if (registered.mfaEnabled) {
        if (!mfaCode.trim()) {
          setError('Enter your MFA or backup code');
          mfaInput?.focus();
          clearUser();
          return;
        }
        const result = await AuthApi.verifyMfa(mfaCode.trim());
        storePasswordSession(
          registered.authToken,
          registered.id,
          registered.email,
          registered.displayName ?? registered.email,
          result.mfaEnabled
        );
        setUser({
          id: registered.id,
          email: registered.email,
          name: registered.displayName ?? registered.email,
          mfaEnabled: result.mfaEnabled,
        });
        const target = (location.state as { from?: string } | null)?.from ?? '/projects';
        navigate(target, { replace: true });
      } else {
        navigate('/mfa', { replace: true });
      }
    } catch (err: any) {
      const message = err.message ?? 'Failed to log in';
      setError(message);
      clearUser();
    } finally {
      setSkipRedirect(false);
      setLoading(false);
    }
  };

  const setInitialPassword = async () => {
    if (!email.trim()) {
      setError('Email is required');
      emailInput?.focus();
      return;
    }
    if (!password.trim()) {
      setError('Password is required');
      return;
    }
    if (password !== confirmPassword) {
      setError('Passwords do not match');
      return;
    }

    setLoading(true);
    setError(null);
    setSkipRedirect(true);
    try {
      const registered = await AuthApi.setInitialPassword(email.trim(), password);
      storePasswordSession(
        registered.authToken,
        registered.id,
        registered.email,
        registered.displayName ?? registered.email,
        registered.mfaEnabled
      );
      setUser({
        id: registered.id,
        email: registered.email,
        name: registered.displayName ?? registered.email,
        mfaEnabled: registered.mfaEnabled,
      });
      if (registered.mfaEnabled) {
        if (!mfaCode.trim()) {
          setError('Enter your MFA or backup code');
          mfaInput?.focus();
          clearUser();
          return;
        }
        const result = await AuthApi.verifyMfa(mfaCode.trim());
        storePasswordSession(
          registered.authToken,
          registered.id,
          registered.email,
          registered.displayName ?? registered.email,
          result.mfaEnabled
        );
        setUser({
          id: registered.id,
          email: registered.email,
          name: registered.displayName ?? registered.email,
          mfaEnabled: result.mfaEnabled,
        });
        const target = (location.state as { from?: string } | null)?.from ?? '/projects';
        navigate(target, { replace: true });
      } else {
        navigate('/mfa', { replace: true });
      }
    } catch (err: any) {
      setError(err.message ?? 'Failed to set password');
      clearUser();
    } finally {
      setSkipRedirect(false);
      setLoading(false);
    }
  };

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (mode === 'register') {
      await registerUser();
    } else if (mode === 'login') {
      await loginUser();
    } else {
      await setInitialPassword();
    }
  };

  return (
    <div className="auth-screen" style={authBackgroundStyle}>
      <div className="page auth-panel">
      <h1>
        {mode === 'register' ? 'Create your account' : mode === 'login' ? 'Log in' : 'Set your password'}
      </h1>
      <p className="muted">
        {mode === 'register'
          ? 'Register once, then create and manage your projects under this account.'
          : mode === 'login'
            ? 'Enter your account email, password, and 2FA code to sign in.'
            : 'This account does not have a password yet. Set one to continue.'}
      </p>

      <form className="card stack" style={{ marginTop: 16 }} onSubmit={onSubmit}>
        <label>Email</label>
        <input
          ref={setEmailInput}
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          type="email"
          placeholder="you@example.com"
        />

        {mode === 'register' && (
          <>
            <label>Display name</label>
            <input
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="How should we show your name?"
            />
          </>
        )}

        {(mode === 'register' || mode === 'login' || mode === 'setPassword') && (
          <>
            <label>Password</label>
            <input
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              type="password"
              placeholder="Password"
            />
          </>
        )}

        {(mode === 'register' || mode === 'setPassword') && (
          <>
            <label>Confirm password</label>
            <input
              value={confirmPassword}
              onChange={(e) => setConfirmPassword(e.target.value)}
              type="password"
              placeholder="Confirm password"
            />
          </>
        )}

        {(mode === 'login' || mode === 'setPassword') && (
          <>
            <label>MFA or backup code</label>
            <input
              ref={setMfaInput}
              value={mfaCode}
              onChange={(e) => setMfaCode(e.target.value)}
              placeholder="123456 or ABCDE-FGHIJ"
              inputMode="text"
            />
          </>
        )}

        <button type="submit" disabled={loading}>
          {loading
            ? mode === 'register'
              ? 'Registering...'
              : mode === 'login'
                ? 'Logging in...'
                : 'Saving...'
            : mode === 'register'
              ? 'Register'
              : mode === 'login'
                ? 'Log in'
                : 'Set password'}
        </button>
        {error && <div className="pill" style={{ background: '#fee2e2', color: '#991b1b' }}>{error}</div>}
      </form>

      <div className="card" style={{ marginTop: 12 }}>
        <p className="muted" style={{ margin: 0 }}>
          {mode === 'register' ? 'Already registered? ' : 'New user? '}
          <button
            type="button"
            onClick={() => switchMode(mode === 'register' ? 'login' : 'register')}
            style={{ background: 'none', border: 'none', color: '#2563eb', cursor: 'pointer', padding: 0 }}
          >
            {mode === 'register' ? 'Log in' : 'Register'}
          </button>
          .
        </p>
        {mode === 'login' && (
          <p className="muted" style={{ marginTop: 8 }}>
            Need to set a password?{' '}
            <button
              type="button"
              onClick={() => switchMode('setPassword')}
              style={{ background: 'none', border: 'none', color: '#2563eb', cursor: 'pointer', padding: 0 }}
            >
              Set password
            </button>
            .
          </p>
        )}
      </div>
      {!isDev && (
        <div className="card" style={{ marginTop: 12 }}>
          <p className="muted" style={{ margin: 0 }}>
            Prefer Microsoft sign-in?{' '}
            <button
              type="button"
              onClick={setMicrosoftMode}
              style={{ background: 'none', border: 'none', color: '#2563eb', cursor: 'pointer', padding: 0 }}
            >
              Use Microsoft login
            </button>
            .
          </p>
        </div>
      )}
      </div>
    </div>
  );
}
