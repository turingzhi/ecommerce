import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../../auth/AuthProvider';
import { ErrorNotice } from '../../components/ErrorNotice';
export function LoginPage() {
  const auth = useAuth();
  const navigate = useNavigate();
  const [email, setEmail] = useState(''),
    [password, setPassword] = useState(''),
    [register, setRegister] = useState(false),
    [busy, setBusy] = useState(false),
    [error, setError] = useState<unknown>(null);
  return (
    <section className="panel narrow">
      <h1>{register ? 'Create an account' : 'Sign in'}</h1>
      <p>Your session stays in this tab&#8217;s memory. Reloading signs you out.</p>
      <form
        onSubmit={async (e) => {
          e.preventDefault();
          setBusy(true);
          setError(null);
          try {
            await (register ? auth.register(email, password) : auth.login(email, password));
            navigate('/');
          } catch (e) {
            setError(e);
          } finally {
            setBusy(false);
          }
        }}
      >
        <label>
          Email
          <input
            type="email"
            required
            autoComplete="username"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
          />
        </label>
        <label>
          Password
          <input
            type="password"
            required
            minLength={6}
            autoComplete={register ? 'new-password' : 'current-password'}
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </label>
        <ErrorNotice error={error} />
        <button disabled={busy}>
          {busy ? 'Please wait&#8230;' : register ? 'Register' : 'Sign in'}
        </button>
        <button
          className="secondary"
          type="button"
          disabled={busy}
          onClick={() => setRegister(!register)}
        >
          {register ? 'Use existing account' : 'Create account'}
        </button>
      </form>
    </section>
  );
}
