#include "GameBanana.h"
#include "NexusMods.h"
#include "Rename.h"
#include <cstdlib> // for std::getenv
#include <filesystem>
#include <iostream>
#include <sstream> // for std::istringstream if we parse user input
#include <string>
#include <vector>

namespace fs = std::filesystem;

// Define the global API_KEY declared in NexusMods.h
std::string API_KEY = "";

std::string sanitizeFileName(const std::string& name)
{
    std::string sanitized = name;
    for (char& c : sanitized) {
        if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '\"' || c == '<' || c == '>' || c == '|') {
            c = '_';
        }
    }
    return sanitized;
}

//--------------------------------------------------
// Helper function: get the default mods directory
//--------------------------------------------------
std::string getDefaultModsDirectory()
{
    // If $HOME is not set, fallback to empty string
    const char* homeEnv = std::getenv("HOME");
    std::string homeDir = (homeEnv ? std::string(homeEnv) : std::string(""));

    // Construct ~/Games/Mods-Lists as the default path
    fs::path defaultPath = fs::path(homeDir) / "Games" / "Mods-Lists";
    return defaultPath.string();
}

//--------------------------------------------------
// Run all GameBanana steps in one sequence
//--------------------------------------------------
void runGameBananaSequence()
{
    // 1) Initialize GameBanana
    initialize();
    std::cout << "GameBanana initialized.\n";

    // 2) Fetch GameBanana user ID from the environment variable
    const char* envUserId = std::getenv("GB_USER_ID");
    std::string userId = (envUserId ? std::string(envUserId) : "");

    if (userId.empty()) {
        std::cerr << "Error: GB_USER_ID environment variable is not set.\n";
        return;
    }
    std::cout << "Using GameBanana user ID from environment: " << userId << "\n";

    // 3) Fetch all subscribed mods
    auto mods = fetchSubscribedMods(userId);
    if (mods.empty()) {
        std::cout << "No subscribed mods found for user ID: " << userId << "\n";
        return;
    }

    std::cout << "Subscribed Mods:\n";
    for (const auto& mod : mods) {
        std::cout << "Profile URL: " << mod.first << " | Mod Name: " << mod.second << "\n";
    }

    // 4) Set base directory for downloads
    std::string defaultModsDir = getDefaultModsDirectory();
    std::cout << "Enter the base directory to download to (Press ENTER for default: "
              << defaultModsDir << "): ";

    std::string baseDir;
    std::getline(std::cin, baseDir);
    if (baseDir.empty()) {
        baseDir = defaultModsDir;
    }

    // 5) Download all detected mods
    std::cout << "\nStarting download of all subscribed mods...\n";

    for (const auto& mod : mods) {
        std::string modUrl = mod.first;
        std::string modName = sanitizeFileName(mod.second);

        // Extract Mod ID from URL
        std::string modId = extractModId(modUrl);
        if (modId.empty()) {
            std::cerr << "Warning: Failed to extract mod ID from URL: " << modUrl << "\n";
            continue;
        }

        std::cout << "Downloading Mod: " << modName << " (ID: " << modId << ")...\n";
        downloadModFiles(modId, modName, baseDir);
    }

    std::cout << "\nAll subscribed mods have been downloaded to: " << baseDir << "\n";

    // 6) Cleanup
    cleanup();
    std::cout << "GameBanana cleanup complete.\n";
}

//--------------------------------------------------
// Detect API key from environment variable
//--------------------------------------------------
std::string detectApiKeyFromEnv()
{
    const char* envApiKey = std::getenv("API_KEY");
    return (envApiKey ? std::string(envApiKey) : std::string(""));
}

//--------------------------------------------------
// Helper: Run the NexusMods workflow for a single domain
//--------------------------------------------------
void runNexusModsForOneDomain(const std::vector<int>& trackedMods, const std::string& gameDomain)
{
    // Get file IDs
    auto fileIdsMap = get_file_ids(trackedMods, gameDomain);

    // Generate download links
    auto downloadLinks = generate_download_links(fileIdsMap, gameDomain);
    std::cout << "\nGenerated Download Links for domain '" << gameDomain << "':\n";
    for (auto& [modFilePair, url] : downloadLinks) {
        std::cout << "  ModID: " << modFilePair.first
                  << ", FileID: " << modFilePair.second
                  << " => " << url << "\n";
    }

    // Save download links
    save_download_links(downloadLinks, gameDomain);
    std::cout << "Download links saved for domain '" << gameDomain << "'.\n";

    // Download files
    download_files(gameDomain);
    std::cout << "Files downloaded for domain '" << gameDomain << "'.\n";
}

//--------------------------------------------------
// Run the NexusMods steps for multiple domains
//--------------------------------------------------
void runNexusModsSequence(const std::vector<std::string>& domains)
{
    // 1) Try detecting API_KEY from the environment
    std::string envApi = detectApiKeyFromEnv();
    if (!envApi.empty()) {
        API_KEY = envApi;
        std::cout << "Detected environment variable API_KEY. Using that.\n";
    } else {
        // Otherwise, fall back to asking the user
        std::cout << "Environment variable API_KEY not set.\n"
                  << "Please enter your NexusMods API Key: ";
        std::cin >> API_KEY;
    }
    std::cout << "API Key set to: " << API_KEY << "\n";

    // 2) Get tracked mods once
    std::vector<int> trackedMods = get_tracked_mods();
    std::cout << "Tracked Mods (IDs):\n";
    for (int modId : trackedMods) {
        std::cout << "  " << modId << "\n";
    }

    // 3) Run the pipeline for each domain
    for (const auto& domain : domains) {
        std::cout << "\n===== Processing Domain: " << domain << " =====\n";
        runNexusModsForOneDomain(trackedMods, domain);
    }
}

//--------------------------------------------------
// Run all Rename steps in one sequence
//--------------------------------------------------
void runRenameSequence()
{
    fs::path modsDir = getDefaultModsDirectory();
    std::cout << "Using mods directory: " << modsDir << "\n";

    auto gameDomains = getGameDomainNames(modsDir);
    if (gameDomains.empty()) {
        std::cerr << "No game domains found in: " << modsDir << "\n";
        return;
    }

    for (const auto& gameDomain : gameDomains) {
        fs::path gameDomainPath = modsDir / gameDomain;
        std::cout << "\nProcessing game domain: " << gameDomain << "\n";

        auto modIDs = getModIDs(gameDomainPath);
        if (modIDs.empty()) {
            std::cerr << "No mod IDs found in: " << gameDomainPath << "\n";
            continue;
        }

        for (const auto& modID : modIDs) {
            std::cout << "\nFetching mod name for modID: " << modID << "\n";
            std::string jsonResponse = fetchModName(gameDomain, modID);
            std::cout << "JSON response: " << jsonResponse << "\n";

            std::string rawModName = extractModName(jsonResponse);
            if (rawModName.empty()) {
                std::cerr << "No mod name found for modID: " << modID << "\n";
                continue;
            }

            std::string modName = sanitizeFileName(rawModName);
            fs::path oldPath = gameDomainPath / modID;
            fs::path newPath = gameDomainPath / modName;
            std::cout << "Renaming: " << oldPath << " -> " << newPath << "\n";

            try {
                fs::rename(oldPath, newPath);
                std::cout << "Renamed " << modID << " to " << modName << " in " << gameDomain << "\n";
            } catch (const fs::filesystem_error& e) {
                std::cerr << "Failed to rename " << oldPath << " to " << newPath << ": " << e.what() << "\n";
            }
        }
    }
}

//--------------------------------------------------
// Main
//--------------------------------------------------
int main(int argc, char* argv[])
{
    bool running = true;
    while (running) {
        std::cout << "\n---------------------------------------\n";
        std::cout << "\nAPI_KEY & GB_USER_ID Required: Retrieve these from their respective Accounts\n";
        std::cout << "\n---------------------------------------\n";
        std::cout << "\n============== Main Menu ==============\n";
        std::cout << "1. Run GameBanana Sequence - Requires GB_USER_ID set in Environment\n";
        std::cout << "2. Run NexusMods Sequence - Requires API_KEY set in Environment\n";
        std::cout << "3. Run Rename Sequence - Typically only required after running NexusMods Sequence\n";
        std::cout << "0. Exit\n";
        std::cout << "=======================================\n";
        std::cout << "Enter your choice (0/1/2/3): ";

        int choice;
        std::cin >> choice;

        // Handle invalid input
        if (!std::cin) {
            std::cin.clear();
            std::cin.ignore(10000, '\n');
            std::cout << "Invalid input, please try again.\n";
            continue;
        }

        switch (choice) {
        case 0: {
            running = false;
            break;
        }
        case 1: {
            // Run everything for GameBanana
            runGameBananaSequence();
            break;
        }
        case 2: {
            // Gather any additional domains from argv (if provided)
            // e.g., if user ran: ./MyProgram horizonzerodawn finalfantasyxx2hdremaster
            // then parse them now.
            std::vector<std::string> gameDomains;

            // Check if there are extra arguments after the "2" in argv
            // The first argument in argv is the executable name
            // The second argument might have been "2"
            // So we start scanning after that
            // e.g.  ./MyProgram 2 horizonzerodawn finalfantasyxx2hdremaster
            //       argv[0] = "./MyProgram"
            //       argv[1] = "2"
            //       argv[2] = "horizonzerodawn"
            //       argv[3] = "finalfantasyxx2hdremaster"
            // => we start from i=2
            for (int i = 2; i < argc; i++) {
                gameDomains.push_back(argv[i]);
            }

            // If none were provided in argv, prompt user for one or more domains
            if (gameDomains.empty()) {
                std::cout << "Enter one or more game domains (space-separated), then press ENTER:\n";
                std::cin.ignore(std::numeric_limits<std::streamsize>::max(), '\n');
                std::string domainsLine;
                std::getline(std::cin, domainsLine);

                std::istringstream iss(domainsLine);
                std::string domain;
                while (iss >> domain) {
                    gameDomains.push_back(domain);
                }
            }

            // If the user still provided nothing, we can bail out or ask again
            if (gameDomains.empty()) {
                std::cout << "No domains specified. Returning to main menu.\n";
                break;
            }

            // Now we have a list of domains. Pass them all to runNexusModsSequence
            runNexusModsSequence(gameDomains);

            // Optionally, stop the loop
            // running = false;
            break;
        }
        case 3: {
            runRenameSequence();
            break;
        }
        default: {
            std::cout << "Invalid choice. Please try again.\n";
            break;
        }
        }
    }

    return 0;
}
