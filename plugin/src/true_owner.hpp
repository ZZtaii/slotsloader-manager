// SF6 Slots -- what the ACCOUNT really owns, game outfits and colours, for the network decision (carrier.hpp decide; Wael
// 24/11/2026: "si on a vraiment le costume 2, il faut que l'adversaire le voie", an outfit or a colour only an unlock-all script
// gives goes on the network as the carrier, in Color 1 / 2).
//
// The game's own answers (CostumeInventory.GetCostumeNoList, ColorInventory's lists, ownership.hpp) count an outfit or a colour a
// DLC gives when DlcManager.IsValidDlcItem says yes; an unlock-all Lua script forces that (and IsValidDlc, IsValidPackageDlc,
// IsNoPackageDlc, IsSubscribed, IsPlayGoMode) to true. Online REFramework stops Lua, and the server answers the communication
// error 50301-30003 when such an outfit or colour is sent. A native hook of ours answering instead was tried (10/11): REFramework
// runs the Lua post-hook after ours whatever the order they were added in, so the game's own logic cannot be asked honestly. What
// is read instead (probes g98 - g102, 10/11; the colours' read in the running game 24/11/2026):
//   - DlcManager's tables (its fields, never its hooked methods): DlcItemCostume {Costume: costume record, Dlc}, DlcItemColor
//     {Color: colour record, Dlc}, DlcDataMain {id, SteamAppId} ("" off Steam: Ryu's Outfit 2 and 3 have DLC 8 alone, a
//     PlayStation pack; on Steam they are bought, never given);
//   - Steam (the game's own steam_api64.dll, its flat API: SteamApps_v008, BIsSubscribedApp / BIsDlcInstalled): the DLCs the
//     account owns. Native: a Lua hook cannot change it;
//   - the inventories' own records and save (not DlcManager's): the costume records (CostumeInventory.GetMasterDataRecord:
//     fighterId, costumeNo, isDefault), every colour record of an outfit (TableDataManager.GetFighterCostumeColors(record, false):
//     id, colorNo, isDefault), what is bought (CostumeInventory.ExistsSaveData, ColorInventory.get_SaveDataList).
// An outfit or a colour is the account's: a default one, one in the save, one no DLC gives (the game lists it on grounds the
// unlock-all script does not touch), or one a DLC the account owns on Steam gives. Outfit 1 and Colors 1 and 2 (numbers 0 and
// 1, no colour record) are every player's. Not known (the tables not read yet or not readable, no record for it, a DLC to ask
// Steam about with no Steam): Own::Unknown, never "owned" as the old answer of this module was for the Random outfit in ranked
// matches (10/11 - 22/11): not known is not sent.
//
// The reads (game calls) happen in refresh(), on the game thread; outfit() and colour() answer from the numbers kept, under the
// module's own lock, from any thread (the alias's hooks run on the flow map's).
#pragma once

#include "carrier.hpp"

namespace slots::trueown {

using carrier::Own;

enum class State : uint8_t { Waiting, Ready, Failed };   // Waiting: DlcManager not initialized yet; Failed: its tables not read

// Game thread: the game's tables read once (until then Waiting, asked again at the next call), then what the save and Steam say
// now (a purchase since the last call). True when an answer may have changed since the last call. `why`: for the log.
bool refresh(const char* why);
State state();

// Outfit `cos` of fighter `fid` is the account's (rules above). Any thread.
Own outfit(int fid, int cos);
// Colour `colour` (0-based: 0 = Color 1) of outfit `cos` of fighter `fid` is the account's, the outfit itself aside. Any thread.
Own colour(int fid, int cos, int colour);

}  // namespace slots::trueown
