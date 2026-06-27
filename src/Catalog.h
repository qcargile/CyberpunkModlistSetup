#pragma once

#include "Config.h"

#include "CleanCore.h"

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <vector>

namespace cbsetup {

enum class Status { Unknown, OK, Missing, Warning, Working, Done, Failed, Manual };

enum class Group { Check, Dependency, Tweak, CleanInstall, Manager, Manual };

enum class Applies { Shared, MO2, Vortex };

struct CleanPreflight {
    bool                        ok = false;
    std::vector<std::string>    blockers;
    cleanslate::FullCleanReport plan;
};

struct Step {
    std::string id;
    std::string title;
    std::string detail;
    Group       group        = Group::Check;
    Applies     applies      = Applies::Shared;
    bool        autoFixable  = false;
    Status      status       = Status::Unknown;
    std::string statusText;
    std::string guide;
    std::string url;
};

struct Model {
    ListConfig   config;
    ListId       list = ListId::None;
    Mode         mode = Mode::None;
    Platform     platform = Platform::Unknown;

    std::wstring gameDir;
    char         installPath[1024]   = {};
    char         downloadsPath[1024] = {};
    char         wjPath[1024]        = {};
    std::vector<std::string> userDone;

    std::vector<Step> steps;

    std::atomic<bool>     busy{false};
    std::atomic<bool>     pendingRedetect{false};
    std::atomic<bool>     cancelRequested{false};
    std::atomic<bool>     summaryPending{false};
    std::atomic<uint64_t> dlDone{0};
    std::atomic<uint64_t> dlTotal{0};

    std::mutex  logMtx;
    std::string log;
    std::string activeLabel;

    bool         avChecked      = false;
    std::string  thirdPartyAv;

    bool         optSysInstalls = true;
    bool         optLongPaths   = true;
    bool         optSteamTweaks = true;
    bool         optDownload    = true;
    bool         optClean       = false;

    bool         cleanScanned   = false;
    uint32_t     cleanCount     = 0;
    bool         wabbajackReady = false;
    std::wstring wabbajackExe;
    bool         vortexReady    = false;
    std::wstring vortexExe;
    bool         vortexManagingCp = false;
    std::wstring vortexDownloadsDir;
    std::string  gpuVendor;

    cleanslate::FullCleanReport cleanScanReport;
    std::vector<std::string>    cleanAppData;
    CleanPreflight cleanPreflight;
};

bool StepVisible(const Model& m, const Step& s);

const char* WabbajackGalleryUrl();

const char* VortexCollectionsUrl();

void BuildCatalog(Model& m);
void DetectAll(Model& m);
void SuggestPaths(Model& m);
void LoadState(Model& m);
void SaveState(Model& m);

Step* Find(Model& m, const char* id);

void Append(Model& m, const std::string& line);
void SetLabel(Model& m, const std::string& label);

void ApplyStep(Model& m, const std::string& id);
void RunAdminBatch(Model& m, const std::vector<std::string>& ops);
void RunAllAuto(Model& m);
void RunCleanScan(Model& m);
CleanPreflight PrecheckClean(Model& m);
void RunFullClean(Model& m);
void DownloadWabbajack(Model& m);
void DownloadVortex(Model& m);
void InstallVortexCollection(Model& m);
void InstallGpuApp(Model& m);
void UndoAll(Model& m);
void LaunchWabbajack(Model& m);
void SendListToWabbajack(Model& m);

}
