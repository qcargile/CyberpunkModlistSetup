#include "Config.h"

namespace cbsetup {

ListConfig ConfigForList(ListId id) noexcept {
    ListConfig c;
    if (id == ListId::ChromeAndBlood) {
        c.name = "Chrome & Blood";
        c.requiredGB = 160;
        c.website = "https://qcargile.github.io/Chrome-Blood/install.html";
        c.discord = "https://discord.gg/U65Nhdcns8";
        c.wabbajackList = "Chrome & Blood";
        c.wabbajackUrl = "https://www.wabbajack.org/search/Chrome_and_Blood/SchoolBoyQ";
        c.collectionUrl = "";
        c.gameVersion = "2.31a";
        c.requiresPhantomLiberty = true;
        c.loaded = true;
    } else if (id == ListId::Wtnc) {
        c.name = "Welcome to Night City";
        c.requiredGB = 1;
        c.website = "https://www.wabbajack.org/modlist/CDPR_Modlists/CyberpunkTHING";
        c.discord = "https://discord.com/invite/eJdMQKnQVt";
        c.wabbajackList = "Welcome to Night City";
        c.wabbajackUrl = "https://www.wabbajack.org/search/CDPR_Modlists/CyberpunkTHING";
        c.collectionUrl = "https://www.nexusmods.com/games/cyberpunk2077/collections/iszwwe";
        c.gameVersion = "2.31a";
        c.requiresPhantomLiberty = false;
        c.loaded = true;
    }
    return c;
}

}
