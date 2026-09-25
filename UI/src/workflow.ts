export type WorkflowStage = 0 | 1 | 2 | 3;

export type WorkflowModel = {
  progressStage: WorkflowStage;
  stageAvailability: readonly [boolean, boolean, boolean, boolean];
};

type WorkflowFacts = {
  plannerMode: boolean;
  polygonValid: boolean;
  pathsBuilt: boolean;
  decorationsBuilt: boolean;
};

/** Derives navigation progress and reachability from the durable workflow facts. */
export const deriveWorkflowModel = ({ plannerMode, polygonValid,
  pathsBuilt, decorationsBuilt }: WorkflowFacts): WorkflowModel => ({
  progressStage: (decorationsBuilt ? 3 : pathsBuilt ? 2 : plannerMode ? 1 : 0) as WorkflowStage,
  stageAvailability: [!pathsBuilt, polygonValid, pathsBuilt, decorationsBuilt],
});
