import { initializeApp } from 'firebase/app';
import { getAuth, signInWithEmailAndPassword } from 'firebase/auth';

export async function signIn(email: string, password: string) {
  const app = initializeApp({ apiKey: 'demo' });
  return signInWithEmailAndPassword(getAuth(app), email, password);
}
