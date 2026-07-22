#define GAMEINPUT_API_VERSION 3
#include <GameInput.h>
#include <Windows.h>
#include <wrl/client.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <string>
#include <thread>

using Microsoft::WRL::ComPtr;
using namespace GameInput::v3;
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
        std::cout
            << "{\"type\":\"ready\",\"apiVersion\":3,"
            << "\"hasFourPaddles\":true,"
            << "\"hasLowFrequencyRumble\":true,"
            << "\"hasHighFrequencyRumble\":true,"
            << "\"hasLeftTriggerRumble\":true,"
            << "\"hasRightTriggerRumble\":true}"
            << std::endl;
    }

    void EmitConnection(bool connected)
    {
        std::cout
            << "{\"type\":\"" << (connected ? "connected" : "disconnected")
            << "\",\"deviceId\":\"gameinput:primary\"}"
            << std::endl;
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
        return std::sscanf(number, "%f", &value) == 1;
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

    EmitReady();
    std::thread(ReadCommands).detach();

    bool connected = false;
    bool hadState = false;
    GameInputGamepadState previousState{};

    while (g_running)
    {
        const auto device = GetDeviceSnapshot();
        ComPtr<IGameInputReading> reading;
        const HRESULT result = gameInput->GetCurrentReading(
            GameInputKindGamepad,
            device.Get(),
            &reading);

        if (FAILED(result))
        {
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
            EmitConnection(true);
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
    return 0;
}
