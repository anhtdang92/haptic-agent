#include <GameInput.h>
#include <Windows.h>
#include <wrl/client.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <string>
#include <thread>

#ifndef GAMEINPUT_API_VERSION
#define GAMEINPUT_API_VERSION 0
#endif

#if GAMEINPUT_API_VERSION == 1
using namespace GameInput::v1;
#elif GAMEINPUT_API_VERSION == 2
using namespace GameInput::v2;
#elif GAMEINPUT_API_VERSION == 3
using namespace GameInput::v3;
#endif

using Microsoft::WRL::ComPtr;
using namespace std::chrono_literals;

namespace
{
    std::atomic_bool g_running{true};
    std::mutex g_deviceMutex;
    ComPtr<IGameInputDevice> g_device;

    struct ButtonBinding
    {
        GameInputGamepadButtons button;
        const char* name;
    };

    constexpr ButtonBinding ButtonBindings[] =
    {
        {GameInputGamepadMenu, "Menu"},
        {GameInputGamepadView, "View"},
        {GameInputGamepadA, "A"},
        {GameInputGamepadB, "B"},
        {GameInputGamepadX, "X"},
        {GameInputGamepadY, "Y"},
        {GameInputGamepadDPadUp, "DPadUp"},
        {GameInputGamepadDPadDown, "DPadDown"},
        {GameInputGamepadDPadLeft, "DPadLeft"},
        {GameInputGamepadDPadRight, "DPadRight"},
        {GameInputGamepadLeftShoulder, "LeftShoulder"},
        {GameInputGamepadRightShoulder, "RightShoulder"},
        {GameInputGamepadLeftThumbstick, "LeftThumbstickButton"},
        {GameInputGamepadRightThumbstick, "RightThumbstickButton"},
        {GameInputGamepadPaddleLeft1, "PaddleLeft1"},
        {GameInputGamepadPaddleLeft2, "PaddleLeft2"},
        {GameInputGamepadPaddleRight1, "PaddleRight1"},
        {GameInputGamepadPaddleRight2, "PaddleRight2"},
    };

    BOOL WINAPI ConsoleControlHandler(DWORD) noexcept
    {
        g_running = false;
        return TRUE;
    }

    void EmitReady()
    {
        // hasFourPaddles is false by evidence, not omission: on Windows the
        // GameInput redistributable never populates the GameInputGamepadPaddle*
        // flags (or any raw controller-button slot) for the Elite Series 2 over
        // USB. Unmapped paddles transmit nothing; mapped paddles arrive as the
        // face buttons assigned in the Xbox Accessories profile. Verified
        // 2026-07-24 (Win 11 26200, redist 3.x). The paddle bindings below stay
        // so paddles light up automatically if a future redist adds support.
        std::cout
            << "{\"type\":\"ready\",\"apiVersion\":" << GAMEINPUT_API_VERSION << ","
            << "\"hasFourPaddles\":false,"
            << "\"hasLowFrequencyRumble\":true,"
            << "\"hasHighFrequencyRumble\":true,"
            << "\"hasLeftTriggerRumble\":true,"
            << "\"hasRightTriggerRumble\":true}"
            << std::endl;
    }

    // Minimal JSON string escaping: device names are vendor-supplied, so they
    // must not be trusted to be free of quotes or control characters.
    std::string EscapeJson(const char* text, size_t length)
    {
        std::string escaped;
        escaped.reserve(length + 8);
        for (size_t index = 0; index < length && text[index] != '\0'; ++index)
        {
            const unsigned char character = static_cast<unsigned char>(text[index]);
            switch (character)
            {
                case '"': escaped += "\\\""; break;
                case '\\': escaped += "\\\\"; break;
                case '\b': escaped += "\\b"; break;
                case '\f': escaped += "\\f"; break;
                case '\n': escaped += "\\n"; break;
                case '\r': escaped += "\\r"; break;
                case '\t': escaped += "\\t"; break;
                default:
                    if (character < 0x20)
                    {
                        char buffer[7];
                        sprintf_s(buffer, "\\u%04x", character);
                        escaped += buffer;
                    }
                    else
                    {
                        escaped += static_cast<char>(character);
                    }
                    break;
            }
        }

        return escaped;
    }

    // GameInput's displayName is frequently null on the PC redistributable, so
    // the vendor/product ids travel too and let the managed host name known
    // hardware instead of falling back to a generic label.
    struct DeviceIdentity
    {
        std::string displayName;
        uint16_t vendorId = 0;
        uint16_t productId = 0;
        GameInputSystemButtons supportedSystemButtons = GameInputSystemButtonNone;
    };

    DeviceIdentity DescribeDevice(const ComPtr<IGameInputDevice>& device)
    {
        DeviceIdentity identity;
        if (!device)
        {
            return identity;
        }

        const GameInputDeviceInfo* info = nullptr;
        if (FAILED(device->GetDeviceInfo(&info)) || info == nullptr)
        {
            return identity;
        }

        identity.vendorId = info->vendorId;
        identity.productId = info->productId;
        identity.supportedSystemButtons = info->supportedSystemButtons;
        if (info->displayName != nullptr)
        {
            constexpr size_t maxNameBytes = 256;
            identity.displayName = EscapeJson(
                info->displayName,
                strnlen_s(info->displayName, maxNameBytes));
        }

        return identity;
    }

    void EmitConnection(bool connected, const DeviceIdentity& identity = {})
    {
        std::cout
            << "{\"type\":\"" << (connected ? "connected" : "disconnected")
            << "\",\"deviceId\":\"gameinput:primary\"";
        if (connected)
        {
            std::cout
                << ",\"name\":\"" << identity.displayName << "\""
                << ",\"vendorId\":" << static_cast<unsigned>(identity.vendorId)
                << ",\"productId\":" << static_cast<unsigned>(identity.productId)
                << ",\"hasGuideButton\":"
                << (((identity.supportedSystemButtons & GameInputSystemButtonGuide) != 0)
                        ? "true"
                        : "false");
        }

        std::cout << "}" << std::endl;
    }

    void EmitButton(const char* control, bool pressed)
    {
        std::cout
            << "{\"type\":\"button\",\"deviceId\":\"gameinput:primary\","
            << "\"control\":\"" << control << "\","
            << "\"pressed\":" << (pressed ? "true" : "false") << "}"
            << std::endl;
    }

    void EmitAxis(const char* control, float value)
    {
        std::cout
            << std::fixed << std::setprecision(5)
            << "{\"type\":\"axis\",\"deviceId\":\"gameinput:primary\","
            << "\"control\":\"" << control << "\","
            << "\"value\":" << value << "}"
            << std::endl;
    }

    // The guide/Xbox button is not part of GameInputGamepadButtons; it arrives
    // only through the system-button callback, and only if the focus policy
    // asks for it. Steam and the Xbox Game Bar also hook it globally, so a
    // press may still never reach us.
    void CALLBACK SystemButtonCallback(
        GameInputCallbackToken,
        void*,
        IGameInputDevice*,
        uint64_t,
        GameInputSystemButtons currentButtons,
        GameInputSystemButtons previousButtons)
    {
        const bool wasPressed = (previousButtons & GameInputSystemButtonGuide) != 0;
        const bool isPressed = (currentButtons & GameInputSystemButtonGuide) != 0;
        if (wasPressed != isPressed)
        {
            EmitButton("Guide", isPressed);
        }
    }

    ComPtr<IGameInputDevice> GetDeviceSnapshot()
    {
        std::scoped_lock lock(g_deviceMutex);
        return g_device;
    }

    void SetDevice(const ComPtr<IGameInputDevice>& device)
    {
        std::scoped_lock lock(g_deviceMutex);
        g_device = device;
    }

    void ClearDevice()
    {
        std::scoped_lock lock(g_deviceMutex);
        g_device.Reset();
    }

    void SetRumble(float low, float high, float leftTrigger, float rightTrigger)
    {
        const auto device = GetDeviceSnapshot();
        if (!device)
        {
            return;
        }

        const GameInputRumbleParams params
        {
            std::clamp(low, 0.0f, 1.0f),
            std::clamp(high, 0.0f, 1.0f),
            std::clamp(leftTrigger, 0.0f, 1.0f),
            std::clamp(rightTrigger, 0.0f, 1.0f),
        };
        device->SetRumbleState(&params);
    }

    bool TryReadFloat(const std::string& line, const char* property, float& value)
    {
        const std::string token = std::string("\"") + property + "\":";
        const auto start = line.find(token);
        if (start == std::string::npos)
        {
            return false;
        }

        const char* number = line.c_str() + start + token.size();
        return sscanf_s(number, "%f", &value) == 1;
    }

    void ReadCommands()
    {
        std::string line;
        while (g_running && std::getline(std::cin, line))
        {
            if (line.find("\"type\":\"stop\"") != std::string::npos)
            {
                SetRumble(0.0f, 0.0f, 0.0f, 0.0f);
                continue;
            }

            if (line.find("\"type\":\"rumble\"") == std::string::npos)
            {
                continue;
            }

            float low = 0.0f;
            float high = 0.0f;
            float leftTrigger = 0.0f;
            float rightTrigger = 0.0f;
            if (TryReadFloat(line, "low", low) &&
                TryReadFloat(line, "high", high) &&
                TryReadFloat(line, "leftTrigger", leftTrigger) &&
                TryReadFloat(line, "rightTrigger", rightTrigger))
            {
                SetRumble(low, high, leftTrigger, rightTrigger);
            }
        }

        g_running = false;
    }

    void EmitButtonChanges(
        GameInputGamepadButtons previous,
        GameInputGamepadButtons current,
        bool hadPrevious)
    {
        for (const auto& binding : ButtonBindings)
        {
            const bool wasPressed = hadPrevious && (previous & binding.button) != 0;
            const bool isPressed = (current & binding.button) != 0;
            if (wasPressed != isPressed)
            {
                EmitButton(binding.name, isPressed);
            }
        }
    }

    void EmitAxisIfChanged(
        const char* name,
        float previous,
        float current,
        bool hadPrevious)
    {
        constexpr float epsilon = 0.0125f;
        if (!hadPrevious || std::abs(current - previous) >= epsilon)
        {
            EmitAxis(name, current);
        }
    }

    void EmitAxisChanges(
        const GameInputGamepadState& previous,
        const GameInputGamepadState& current,
        bool hadPrevious)
    {
        EmitAxisIfChanged("LeftTrigger", previous.leftTrigger, current.leftTrigger, hadPrevious);
        EmitAxisIfChanged("RightTrigger", previous.rightTrigger, current.rightTrigger, hadPrevious);
        EmitAxisIfChanged("LeftThumbstickX", previous.leftThumbstickX, current.leftThumbstickX, hadPrevious);
        EmitAxisIfChanged("LeftThumbstickY", previous.leftThumbstickY, current.leftThumbstickY, hadPrevious);
        EmitAxisIfChanged("RightThumbstickX", previous.rightThumbstickX, current.rightThumbstickX, hadPrevious);
        EmitAxisIfChanged("RightThumbstickY", previous.rightThumbstickY, current.rightThumbstickY, hadPrevious);
    }
}

int main()
{
    SetConsoleCtrlHandler(ConsoleControlHandler, TRUE);

    ComPtr<IGameInput> gameInput;
    const HRESULT createResult = GameInputCreate(&gameInput);
    if (FAILED(createResult))
    {
        std::cerr << "GameInputCreate failed with HRESULT 0x"
                  << std::hex << static_cast<unsigned long>(createResult)
                  << std::endl;
        return 1;
    }

    // Ask for background input and the guide button. Without this the guide
    // button is reserved by the shell, and readings stop whenever the host
    // window loses focus.
    gameInput->SetFocusPolicy(static_cast<GameInputFocusPolicy>(
        GameInputEnableBackgroundInput | GameInputEnableBackgroundGuideButton));

    GameInputCallbackToken systemButtonToken = 0;
    const HRESULT systemButtonResult = gameInput->RegisterSystemButtonCallback(
        nullptr,
        GameInputSystemButtonGuide,
        nullptr,
        SystemButtonCallback,
        &systemButtonToken);
    if (FAILED(systemButtonResult))
    {
        // Not fatal: everything except the guide button still works.
        std::cerr << "RegisterSystemButtonCallback failed with HRESULT 0x"
                  << std::hex << static_cast<unsigned long>(systemButtonResult)
                  << std::dec << std::endl;
    }

    EmitReady();
    std::thread(ReadCommands).detach();

    bool connected = false;
    bool hadState = false;
    GameInputGamepadState previousState{};
    HRESULT lastLoggedFailure = S_OK;

    while (g_running)
    {
        const auto device = GetDeviceSnapshot();
        ComPtr<IGameInputReading> reading;
        const HRESULT result = gameInput->GetCurrentReading(
            static_cast<GameInputKind>(GameInputKindGamepad | GameInputKindController),
            device.Get(),
            &reading);

        if (FAILED(result))
        {
            // Log each distinct failure once so the managed host's stderr
            // drain shows why no gamepad reading is available (e.g. reading
            // not yet generated vs. device unsupported by this transport).
            if (result != lastLoggedFailure)
            {
                lastLoggedFailure = result;
                std::cerr << "GetCurrentReading failed with HRESULT 0x"
                          << std::hex << static_cast<unsigned long>(result)
                          << std::dec << std::endl;
            }

            if (connected)
            {
                connected = false;
                hadState = false;
                SetRumble(0.0f, 0.0f, 0.0f, 0.0f);
                ClearDevice();
                EmitConnection(false);
            }

            std::this_thread::sleep_for(100ms);
            continue;
        }

        if (!device)
        {
            ComPtr<IGameInputDevice> discovered;
            reading->GetDevice(&discovered);
            SetDevice(discovered);
        }

        if (!connected)
        {
            connected = true;
            EmitConnection(true, DescribeDevice(GetDeviceSnapshot()));
        }

        GameInputGamepadState currentState{};
        if (reading->GetGamepadState(&currentState))
        {
            EmitButtonChanges(previousState.buttons, currentState.buttons, hadState);
            EmitAxisChanges(previousState, currentState, hadState);
            previousState = currentState;
            hadState = true;
        }

        std::this_thread::sleep_for(4ms);
    }

    SetRumble(0.0f, 0.0f, 0.0f, 0.0f);
    if (systemButtonToken != 0)
    {
        gameInput->StopCallback(systemButtonToken);
    }

    return 0;
}
