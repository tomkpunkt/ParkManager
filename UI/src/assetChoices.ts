/** Asset catalog DTOs kept separate from workflow rendering. */
export type AssetChoiceOption = { name: string; icon: string };

export type AssetChoiceState = {
  selected: string;
  selectedMany: string[];
  options: AssetChoiceOption[];
};

export type AssetChoiceMap = Record<string, AssetChoiceState>;

export const assetCategories = [
  { key: "tree", payload: "Tree", multi: true },
  { key: "bush", payload: "Bush", multi: true },
  { key: "bench", payload: "Bench", multi: false },
  { key: "lamp", payload: "Lamp", multi: false },
  { key: "fence", payload: "Fence", multi: false },
  { key: "trashbin", payload: "TrashBin", multi: false },
] as const;

export type AssetCategory = typeof assetCategories[number];

/** Parses the version-tolerant JSON bridge and rejects unusable icon tiles. */
export const parseAssetChoices = (json: string): AssetChoiceMap => {
  try {
    const parsed = JSON.parse(json);
    if (!parsed || typeof parsed !== "object") return {};
    const result: AssetChoiceMap = {};
    Object.keys(parsed).forEach((key) => {
      const state = parsed[key];
      if (!state || !Array.isArray(state.options)) return;
      result[key] = {
        selected: typeof state.selected === "string" ? state.selected : "",
        selectedMany: Array.isArray(state.selectedMany)
          ? state.selectedMany.filter((name: unknown): name is string =>
              typeof name === "string")
          : [],
        // Old string payloads may survive for one frame during a hot reload.
        options: state.options.map((option: unknown) => typeof option === "string"
          ? { name: option, icon: "" }
          : {
              name: typeof (option as any)?.name === "string"
                ? (option as any).name : "",
              icon: typeof (option as any)?.icon === "string"
                ? (option as any).icon : "",
            }).filter((option: AssetChoiceOption) =>
              option.name.trim().length > 0 && option.icon.trim().length > 0),
      };
    });
    return result;
  } catch {
    return {};
  }
};
