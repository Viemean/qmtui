#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <ksmedia.h>
#include <fcntl.h>
#include <io.h>
#include <stdint.h>
#include <stdio.h>
#include <cmath>
#include <cstring>
#include <algorithm>

// {00000003-0000-0010-8000-00aa00389b71}
static const GUID LOCAL_KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = 
    { 0x00000003, 0x0000, 0x0010, { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };

#include <vector>

#define TARGET_SAMPLE_RATE 16000

struct ResamplerState {
    double phase;
    float prevSample;
};

static std::vector<float> s_monoSource;
static std::vector<int16_t> s_pcmOut;

static void processAudioFrames(
    const BYTE* pData,
    UINT32 numFrames,
    DWORD flags,
    const WAVEFORMATEX* pwfx,
    ResamplerState& resampler
) {
    if (numFrames == 0) return;

    const bool isSilent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
    const WORD channels = pwfx->nChannels;
    const WORD bitsPerSample = pwfx->wBitsPerSample;
    const DWORD srcRate = pwfx->nSamplesPerSec;

    bool isFloat = false;
    WORD validBits = bitsPerSample;

    if (pwfx->wFormatTag == WAVE_FORMAT_IEEE_FLOAT) {
        isFloat = true;
    } else if (pwfx->wFormatTag == WAVE_FORMAT_EXTENSIBLE) {
        const WAVEFORMATEXTENSIBLE* pExt = (const WAVEFORMATEXTENSIBLE*)pwfx;
        if (memcmp(&pExt->SubFormat, &LOCAL_KSDATAFORMAT_SUBTYPE_IEEE_FLOAT, sizeof(GUID)) == 0) {
            isFloat = true;
        }
        if (pExt->Samples.wValidBitsPerSample > 0) {
            validBits = pExt->Samples.wValidBitsPerSample;
        }
    }

    s_monoSource.resize(numFrames);

    if (isSilent || !pData) {
        std::memset(s_monoSource.data(), 0, numFrames * sizeof(float));
    } else if (isFloat && bitsPerSample == 32) {
        const float* fData = (const float*)pData;
        if (channels == 1) {
            std::memcpy(s_monoSource.data(), fData, numFrames * sizeof(float));
        } else if (channels == 2) {
            for (UINT32 i = 0; i < numFrames; i++) {
                s_monoSource[i] = (fData[i * 2] + fData[i * 2 + 1]) * 0.5f;
            }
        } else {
            for (UINT32 i = 0; i < numFrames; i++) {
                float sum = 0.0f;
                for (WORD ch = 0; ch < channels; ch++) {
                    sum += fData[i * channels + ch];
                }
                s_monoSource[i] = sum / (float)channels;
            }
        }
    } else if (!isFloat && bitsPerSample == 16) {
        const int16_t* sData = (const int16_t*)pData;
        if (channels == 1) {
            for (UINT32 i = 0; i < numFrames; i++) {
                s_monoSource[i] = sData[i] / 32768.0f;
            }
        } else if (channels == 2) {
            for (UINT32 i = 0; i < numFrames; i++) {
                s_monoSource[i] = ((float)sData[i * 2] + (float)sData[i * 2 + 1]) * (0.5f / 32768.0f);
            }
        } else {
            for (UINT32 i = 0; i < numFrames; i++) {
                float sum = 0.0f;
                for (WORD ch = 0; ch < channels; ch++) {
                    sum += sData[i * channels + ch] / 32768.0f;
                }
                s_monoSource[i] = sum / (float)channels;
            }
        }
    } else if (!isFloat && bitsPerSample == 24) {
        for (UINT32 i = 0; i < numFrames; i++) {
            float sum = 0.0f;
            for (WORD ch = 0; ch < channels; ch++) {
                const BYTE* p = pData + (i * channels + ch) * 3;
                int32_t v = (int32_t)((p[0] << 8) | (p[1] << 16) | (p[2] << 24)) >> 8;
                sum += v / 8388608.0f;
            }
            s_monoSource[i] = sum / (float)channels;
        }
    } else if (!isFloat && bitsPerSample == 32) {
        const int32_t* iData = (const int32_t*)pData;
        double scale = validBits == 24 ? 8388608.0 : 2147483648.0;
        int shift = validBits == 24 ? 8 : 0;
        for (UINT32 i = 0; i < numFrames; i++) {
            double sum = 0.0;
            for (WORD ch = 0; ch < channels; ch++) {
                int32_t v = iData[i * channels + ch] >> shift;
                sum += (double)v / scale;
            }
            s_monoSource[i] = (float)(sum / (double)channels);
        }
    } else {
        std::memset(s_monoSource.data(), 0, numFrames * sizeof(float));
    }

    // 线性插值重采样至 16000Hz
    s_pcmOut.clear();
    const double ratio = (double)srcRate / (double)TARGET_SAMPLE_RATE;
    while (resampler.phase < (double)numFrames) {
        const int idx0 = (int)std::floor(resampler.phase);
        const double frac = resampler.phase - (double)idx0;

        float s0 = (idx0 >= 0 && idx0 < (int)numFrames) ? s_monoSource[idx0] : resampler.prevSample;
        int idx1 = idx0 + 1;
        float s1 = (idx1 >= 0 && idx1 < (int)numFrames) ? s_monoSource[idx1] : s0;

        float interpolated = (float)(s0 * (1.0 - frac) + s1 * frac);
        if (interpolated > 1.0f) interpolated = 1.0f;
        if (interpolated < -1.0f) interpolated = -1.0f;

        s_pcmOut.push_back((int16_t)(interpolated < 0.0f ? (interpolated * 32768.0f) : (interpolated * 32767.0f)));
        resampler.phase += ratio;
    }

    resampler.phase -= (double)numFrames;
    if (numFrames > 0) {
        resampler.prevSample = s_monoSource[numFrames - 1];
    }

    if (!s_pcmOut.empty()) {
        size_t written = fwrite(s_pcmOut.data(), sizeof(int16_t), s_pcmOut.size(), stdout);
        if (written != s_pcmOut.size()) {
            exit(0);
        }
        fflush(stdout);
    }
}

enum RecordMode {
    MODE_STOPPED = 0,
    MODE_LOOPBACK = 1,
    MODE_MIC = 2,
    MODE_QUIT = 3
};

static volatile RecordMode g_targetMode = MODE_LOOPBACK;
static volatile bool g_running = true;

int main(int argc, char* argv[]) {
    _setmode(_fileno(stdout), _O_BINARY);

    RecordMode initialMode = MODE_LOOPBACK;
    DWORD parentPid = 0;
    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--mic") == 0 || strcmp(argv[i], "-m") == 0) {
            initialMode = MODE_MIC;
        } else if (strcmp(argv[i], "--idle") == 0) {
            initialMode = MODE_STOPPED;
        } else if (strcmp(argv[i], "--parent-pid") == 0 && i + 1 < argc) {
            parentPid = (DWORD)strtoul(argv[++i], NULL, 10);
        }
    }
    g_targetMode = initialMode;

    HANDLE hParent = NULL;
    if (parentPid > 0) {
        hParent = OpenProcess(SYNCHRONIZE, FALSE, parentPid);
        if (!hParent) {
            return 0;
        }
    }

    wchar_t eventNameLoopback[64];
    wchar_t eventNameMic[64];
    wchar_t eventNameStop[64];
    swprintf(eventNameLoopback, 64, L"Local\\QmTui_Record_Loopback_%lu", parentPid);
    swprintf(eventNameMic, 64, L"Local\\QmTui_Record_Mic_%lu", parentPid);
    swprintf(eventNameStop, 64, L"Local\\QmTui_Record_Stop_%lu", parentPid);

    HANDLE hEvtLoopback = CreateEventW(NULL, FALSE, FALSE, eventNameLoopback);
    HANDLE hEvtMic = CreateEventW(NULL, FALSE, FALSE, eventNameMic);
    HANDLE hEvtStop = CreateEventW(NULL, FALSE, FALSE, eventNameStop);

    HRESULT hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    if (FAILED(hr)) {
        return 1;
    }

    RecordMode currentMode = MODE_STOPPED;
    IAudioClient* pAudioClient = NULL;
    IAudioCaptureClient* pCaptureClient = NULL;
    WAVEFORMATEX* pwfx = NULL;
    ResamplerState resampler = { 0.0, 0.0f };

    auto cleanupAudio = [&]() {
        if (pAudioClient) {
            pAudioClient->Stop();
        }
        if (pCaptureClient) {
            pCaptureClient->Release();
            pCaptureClient = NULL;
        }
        if (pwfx) {
            CoTaskMemFree(pwfx);
            pwfx = NULL;
        }
        if (pAudioClient) {
            pAudioClient->Release();
            pAudioClient = NULL;
        }
        currentMode = MODE_STOPPED;
    };

    auto initAudio = [&](RecordMode mode) -> bool {
        cleanupAudio();
        bool isMicrophone = (mode == MODE_MIC);
        EDataFlow dataFlow = isMicrophone ? eCapture : eRender;
        DWORD streamFlags = isMicrophone ? 0 : AUDCLNT_STREAMFLAGS_LOOPBACK;

        IMMDeviceEnumerator* pEnumerator = NULL;
        HRESULT hrEnum = CoCreateInstance(__uuidof(MMDeviceEnumerator), NULL, CLSCTX_ALL, __uuidof(IMMDeviceEnumerator), (void**)&pEnumerator);
        if (FAILED(hrEnum) || !pEnumerator) return false;

        IMMDevice* pDevice = NULL;
        hrEnum = pEnumerator->GetDefaultAudioEndpoint(dataFlow, eConsole, &pDevice);
        pEnumerator->Release();
        if (FAILED(hrEnum) || !pDevice) return false;

        hrEnum = pDevice->Activate(__uuidof(IAudioClient), CLSCTX_ALL, NULL, (void**)&pAudioClient);
        pDevice->Release();
        if (FAILED(hrEnum) || !pAudioClient) return false;

        hrEnum = pAudioClient->GetMixFormat(&pwfx);
        if (FAILED(hrEnum) || !pwfx) {
            cleanupAudio();
            return false;
        }

        const REFERENCE_TIME hnsRequestedDuration = 10000000;
        hrEnum = pAudioClient->Initialize(AUDCLNT_SHAREMODE_SHARED, streamFlags, hnsRequestedDuration, 0, pwfx, NULL);
        if (FAILED(hrEnum)) {
            cleanupAudio();
            return false;
        }

        hrEnum = pAudioClient->GetService(__uuidof(IAudioCaptureClient), (void**)&pCaptureClient);
        if (FAILED(hrEnum) || !pCaptureClient) {
            cleanupAudio();
            return false;
        }

        hrEnum = pAudioClient->Start();
        if (FAILED(hrEnum)) {
            cleanupAudio();
            return false;
        }

        resampler = { 0.0, 0.0f };
        currentMode = mode;
        return true;
    };

    HANDLE waitHandles[4];
    DWORD waitCount = 0;
    if (hParent) waitHandles[waitCount++] = hParent;
    if (hEvtLoopback) waitHandles[waitCount++] = hEvtLoopback;
    if (hEvtMic) waitHandles[waitCount++] = hEvtMic;
    if (hEvtStop) waitHandles[waitCount++] = hEvtStop;

    while (g_running) {
        DWORD waitRes = WaitForMultipleObjects(waitCount, waitHandles, FALSE, currentMode == MODE_STOPPED ? 100 : 0);
        if (waitRes >= WAIT_OBJECT_0 && waitRes < WAIT_OBJECT_0 + waitCount) {
            HANDLE triggered = waitHandles[waitRes - WAIT_OBJECT_0];
            if (triggered == hParent) {
                break;
            } else if (triggered == hEvtLoopback) {
                g_targetMode = MODE_LOOPBACK;
            } else if (triggered == hEvtMic) {
                g_targetMode = MODE_MIC;
            } else if (triggered == hEvtStop) {
                g_targetMode = MODE_STOPPED;
            }
        } else if (waitRes == WAIT_FAILED) {
            break;
        }

        RecordMode target = g_targetMode;
        if (target != currentMode) {
            if (target == MODE_STOPPED) {
                cleanupAudio();
            } else {
                if (!initAudio(target)) {
                    Sleep(50);
                    continue;
                }
            }
        }

        if (currentMode == MODE_STOPPED) {
            continue;
        }

        UINT32 packetLength = 0;
        hr = pCaptureClient->GetNextPacketSize(&packetLength);
        if (FAILED(hr)) {
            Sleep(10);
            continue;
        }

        if (packetLength == 0) {
            Sleep(10);
            continue;
        }

        while (packetLength != 0 && g_targetMode == currentMode) {
            BYTE* pData = NULL;
            UINT32 numFramesAvailable = 0;
            DWORD flags = 0;

            hr = pCaptureClient->GetBuffer(&pData, &numFramesAvailable, &flags, NULL, NULL);
            if (FAILED(hr)) break;

            processAudioFrames(pData, numFramesAvailable, flags, pwfx, resampler);

            pCaptureClient->ReleaseBuffer(numFramesAvailable);

            hr = pCaptureClient->GetNextPacketSize(&packetLength);
            if (FAILED(hr)) break;
        }
    }

    cleanupAudio();
    if (hEvtLoopback) CloseHandle(hEvtLoopback);
    if (hEvtMic) CloseHandle(hEvtMic);
    if (hEvtStop) CloseHandle(hEvtStop);
    if (hParent) CloseHandle(hParent);
    CoUninitialize();
    return 0;
}
