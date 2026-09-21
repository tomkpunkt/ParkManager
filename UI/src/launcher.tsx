import { useValue } from "cs2/api";
import { FloatingButton } from "cs2/ui";
import { toolActive$, toggleTool } from "./bindings";
import toolbarIcon from "./assets/park-manager.svg";

export const ParkManagerLauncher = () => {
  const active = useValue(toolActive$);

  return (
    <FloatingButton
      src={toolbarIcon}
      selected={active}
      onSelect={toggleTool}
      tooltipLabel="ParkManager"
    />
  );
};
