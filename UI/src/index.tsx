import { ModRegistrar } from "cs2/modding";
import { ParkManagerLauncher } from "./launcher";
import { ParkManagerPanel } from "./panel";

const register: ModRegistrar = (moduleRegistry) => {
  moduleRegistry.append("GameTopLeft", ParkManagerLauncher);
  moduleRegistry.append("Game", ParkManagerPanel);
};

export default register;
