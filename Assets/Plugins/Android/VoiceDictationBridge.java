package com.brenpoly.cardboardxr.system;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.speech.RecognitionListener;
import android.speech.RecognizerIntent;
import android.speech.SpeechRecognizer;
import android.util.Log;

import java.util.ArrayList;
import java.util.Locale;

// Headless Android SpeechRecognizer bridge for VR keyboards (no popup dialog disrupting the stereo view).
public final class VoiceDictationBridge {
    private static final String TAG = "CardboardXRVoice";
    private static final int REQ_AUDIO_PERM = 9241;

    private final Activity mActivity;
    private SpeechRecognizer mRecognizer;
    private volatile boolean mListening;
    private volatile float mVoiceLevel01;
    private volatile String mPendingPartial = "";
    private volatile String mPendingFinal = "";
    private volatile String mStatus = "";
    private boolean mHasNewPartial;
    private boolean mHasNewFinal;

    public VoiceDictationBridge(Activity activity) {
        mActivity = activity;
    }

    public void startListening(final String languageTag) {
        if (mActivity == null) {
            return;
        }
        mActivity.runOnUiThread(() -> {
            try {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M
                        && mActivity.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
                    mListening = false;
                    mVoiceLevel01 = 0f;
                    mStatus = "Concedi il permesso microfono e riprova";
                    mActivity.requestPermissions(new String[] { Manifest.permission.RECORD_AUDIO }, REQ_AUDIO_PERM);
                    return;
                }
                if (!SpeechRecognizer.isRecognitionAvailable(mActivity)) {
                    mListening = false;
                    mVoiceLevel01 = 0f;
                    mStatus = "Dettazione vocale non disponibile";
                    return;
                }
                destroyRecognizerLocked();
                mRecognizer = SpeechRecognizer.createSpeechRecognizer(mActivity);
                mRecognizer.setRecognitionListener(new Listener());

                Intent intent = new Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH);
                intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM);
                String lang = (languageTag != null && !languageTag.isEmpty())
                        ? languageTag
                        : Locale.getDefault().toLanguageTag();
                intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE, lang);
                intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE_PREFERENCE, lang);
                intent.putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true);
                intent.putExtra(RecognizerIntent.EXTRA_MAX_RESULTS, 1);

                mListening = true;
                mVoiceLevel01 = 0.25f;
                mPendingPartial = "";
                mHasNewPartial = false;
                mStatus = "In ascolto...";
                mRecognizer.startListening(intent);
            } catch (Throwable t) {
                Log.w(TAG, "Failed to start voice dictation", t);
                mListening = false;
                mVoiceLevel01 = 0f;
                mStatus = "Errore avvio dettazione";
            }
        });
    }

    public void stopListening() {
        if (mActivity == null) {
            return;
        }
        mActivity.runOnUiThread(() -> {
            mListening = false;
            mVoiceLevel01 = 0f;
            if (mRecognizer != null) {
                try {
                    mRecognizer.stopListening();
                } catch (Throwable ignored) {
                }
            }
        });
    }

    public boolean isListening() {
        return mListening;
    }

    public float getVoiceLevel() {
        return mListening ? mVoiceLevel01 : 0f;
    }

    public synchronized String pollPartialText() {
        if (!mHasNewPartial) {
            return null;
        }
        mHasNewPartial = false;
        return mPendingPartial;
    }

    public synchronized String pollFinalText() {
        if (!mHasNewFinal) {
            return null;
        }
        mHasNewFinal = false;
        String out = mPendingFinal;
        mPendingFinal = "";
        return out;
    }

    public String pollStatus() {
        String s = mStatus;
        return s != null ? s : "";
    }

    public void dispose() {
        if (mActivity == null) {
            return;
        }
        mActivity.runOnUiThread(this::destroyRecognizerLocked);
    }

    private void destroyRecognizerLocked() {
        mListening = false;
        mVoiceLevel01 = 0f;
        if (mRecognizer != null) {
            try {
                mRecognizer.destroy();
            } catch (Throwable ignored) {
            }
            mRecognizer = null;
        }
    }

    private final class Listener implements RecognitionListener {
        @Override public void onReadyForSpeech(Bundle params) { mStatus = "In ascolto..."; }
        @Override public void onBeginningOfSpeech() { mStatus = "Parla ora..."; }
        @Override public void onRmsChanged(float rmsdB) {
            float norm = (rmsdB + 2.0f) / 10.0f;
            if (norm < 0f) norm = 0f;
            if (norm > 1f) norm = 1f;
            mVoiceLevel01 = norm;
        }
        @Override public void onBufferReceived(byte[] buffer) {}
        @Override public void onEndOfSpeech() {
            mVoiceLevel01 = 0.2f;
        }
        @Override public void onError(int error) {
            mListening = false;
            mVoiceLevel01 = 0f;
            switch (error) {
                case SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS:
                    mStatus = "Concedi il permesso microfono";
                    break;
                case SpeechRecognizer.ERROR_NO_MATCH:
                case SpeechRecognizer.ERROR_SPEECH_TIMEOUT:
                    mStatus = "Nessun testo riconosciuto";
                    break;
                case SpeechRecognizer.ERROR_NETWORK:
                case SpeechRecognizer.ERROR_NETWORK_TIMEOUT:
                    mStatus = "Rete assente per riconoscimento vocale";
                    break;
                default:
                    mStatus = "";
                    break;
            }
        }
        @Override public void onResults(Bundle results) {
            ArrayList<String> list = results != null
                    ? results.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)
                    : null;
            String spoken = (list != null && !list.isEmpty() && list.get(0) != null)
                    ? list.get(0).trim()
                    : "";
            synchronized (VoiceDictationBridge.this) {
                mPendingPartial = "";
                mHasNewPartial = true;
                if (!spoken.isEmpty()) {
                    mPendingFinal = mPendingFinal.isEmpty() ? spoken : (mPendingFinal + " " + spoken);
                    mHasNewFinal = true;
                }
            }
            mListening = false;
            mVoiceLevel01 = 0f;
            mStatus = "";
        }
        @Override public void onPartialResults(Bundle partialResults) {
            ArrayList<String> list = partialResults != null
                    ? partialResults.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)
                    : null;
            String partial = (list != null && !list.isEmpty() && list.get(0) != null)
                    ? list.get(0).trim()
                    : "";
            synchronized (VoiceDictationBridge.this) {
                mPendingPartial = partial;
                mHasNewPartial = true;
            }
        }
        @Override public void onEvent(int eventType, Bundle params) {}
    }
}
