#pragma once

#include "Config.h"

#include <cstdint>
#include <string>
#include <vector>

namespace cbsetup {

Platform DetectGameInstall(std::wstring& outDir) noexcept;

Platform ClassifyGameDir(const std::wstring& gameDir) noexcept;

const char* PlatformName(Platform p) noexcept;

bool VcRedistInstalled() noexcept;

std::string DotNet8DesktopVersion() noexcept;

bool DirectXLegacyInstalled() noexcept;

bool WingetAvailable() noexcept;

std::wstring WingetPath() noexcept;

uint64_t FreeBytesOnDrive(const std::wstring& anyPathOnDrive) noexcept;

std::wstring DriveRootOf(const std::wstring& path) noexcept;

std::string DriveFilesystem(const std::wstring& anyPathOnDrive) noexcept;

bool PathUnderCloudSync(const std::wstring& path) noexcept;

bool PathUnderProgramFiles(const std::wstring& path) noexcept;

bool DirWritable(const std::wstring& path) noexcept;

int LongPathsEnabled() noexcept;

std::vector<std::wstring> SteamLibraries() noexcept;

bool RedmodInstalled(const std::wstring& gameDir) noexcept;

int SteamAutoUpdateBehavior() noexcept;

struct SteamGameInfo {
    bool        manifestFound = false;
    std::string buildId;
    std::string targetBuildId;
    std::string betaKey;
    uint64_t    stateFlags = 0;
};

SteamGameInfo ReadSteamGameInfo(const std::wstring& gameDir) noexcept;

bool PhantomLibertyInstalled(const std::wstring& gameDir) noexcept;

std::wstring SteamAppManifestPath(const wchar_t* appid) noexcept;

std::wstring SteamExePath() noexcept;

bool SteamRunning() noexcept;

bool IsProcessRunning(const wchar_t* exeName) noexcept;

int VortexProfileCount() noexcept;

bool VortexCyberpunkExtension() noexcept;

struct VortexCpSettings {
    bool        found = false;
    std::string stagingPath;
    std::string deployMethod;
};

VortexCpSettings ReadVortexCpSettings() noexcept;

std::wstring VortexDownloadsDir() noexcept;

std::string GpuName() noexcept;

}
