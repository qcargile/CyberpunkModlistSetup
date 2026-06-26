#pragma once

#include <cstdint>
#include <string>

namespace cbsetup {

enum class Mode { None, MO2, Vortex };

enum class Platform { Unknown, Steam, GOG, Epic };

enum class ListId { None, ChromeAndBlood, Wtnc };

struct ListConfig {
    std::string name           = "Cyberpunk 2077 Modlist";
    Mode        defaultManager = Mode::None;
    uint64_t    requiredGB     = 120;
    std::string website;
    std::string discord;
    std::string wabbajackList;
    std::string wabbajackUrl;
    std::string collectionUrl;
    std::string collectionRevision;
    std::string vortexUrl;
    std::string nvidiaAppUrl = "https://us.download.nvidia.com/nvapp/client/11.0.7.247/NVIDIA_app_v11.0.7.247.exe";
    std::string amdAppUrl    = "https://drivers.amd.com/drivers/installer/26.10/whql/amd-software-adrenalin-edition-26.6.2-minimalsetup-260620_web.exe";
    std::string expectedBuildId;
    std::string gameVersion;
    bool        requiresPhantomLiberty = false;
    bool        loaded         = false;
};

ListConfig ConfigForList(ListId id) noexcept;

}
