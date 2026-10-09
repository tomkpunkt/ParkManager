import { de } from "./locales/de";
import { en } from "./locales/en";
import { ptBR } from "./locales/pt-BR";
import { getTexts } from "./i18n";
import { formatUiText } from "./uiText";

const failures: string[] = [];
const check = (condition: boolean, message: string) => {
  if (!condition) failures.push(message);
};

const placeholders = (template: string): string =>
  (template.match(/\{(\d+)(?::(\d+))?\}/g) || []).slice().sort().join(",");

check(getTexts("de") === de, "de");
check(getTexts("de-DE") === de, "de-DE");
check(getTexts("de-AT") === de, "de-AT");
check(getTexts("de_CH") === de, "de_CH");
check(getTexts("  de-DE  ") === de, "padded de-DE");
check(getTexts("en") === en, "en");
check(getTexts("en-US") === en, "en-US");
check(getTexts("en-GB") === en, "en-GB");
check(getTexts("DE-DE") === en, "DE-DE stays English");
check(getTexts("debug") === en, "debug is not German");
check(getTexts("den-DE") === en, "den-DE is not German");
check(getTexts("fr-FR") === en, "fr-FR");
check(getTexts("pt-BR") === ptBR, "pt-BR");
check(getTexts("pt") === en, "pt does not use pt-BR");
check(getTexts("pt-PT") === en, "pt-PT does not use pt-BR");
check(getTexts("zh-HANS") === en, "zh-HANS");
check(getTexts("") === en, "empty");
check(getTexts("   ") === en, "blank");
check(getTexts(undefined as unknown as string) === en, "undefined");
check(getTexts("de").close === "ParkManager schließen", "German close");
check(getTexts("en-US").close === "Close ParkManager", "English close");

const messageKeys = Object.keys(en.messages) as (keyof typeof en.messages)[];
check(messageKeys.length === Object.keys(de.messages).length, "message key count");
messageKeys.forEach((key) => {
  check(placeholders(en.messages[key]) === placeholders(de.messages[key]),
    "placeholders de " + key);
  check(placeholders(en.messages[key]) === placeholders(ptBR.messages[key]),
    "placeholders pt-BR " + key);
  check(ptBR.messages[key] !== en.messages[key], "pt-BR translates " + key);
});

const warning = JSON.stringify({
  k: "preflight.warning",
  a: [{ k: "preflight.pathSteep", a: [] }, 12.4, -0.2],
});
check(formatUiText(warning, getTexts("de-DE").messages)
  === "Hinweis: Weg stark geneigt bei X 12, Z 0. Bau wird trotzdem versucht.",
  "German warning");
check(formatUiText(warning, getTexts("en-US").messages)
  === "Note: steep path at X 12, Z 0. Building anyway.",
  "English warning");
check(formatUiText("Validation failed: selected area is blocked.", en.messages)
  === "Validation failed: selected area is blocked.",
  "plain text");
check(formatUiText(warning, getTexts("pt-BR").messages)
  === "Nota: caminho íngreme em X 12, Z 0. Construindo mesmo assim.",
  "Portuguese warning");
check(getTexts("pt-BR").pathsGates(0) === "0 entradas marcadas", "0 entradas");
check(getTexts("pt-BR").pathsGates(1) === "1 entrada marcada", "1 entrada");
check(getTexts("pt-BR").pathsGates(2) === "2 entradas marcadas", "2 entradas");
check(getTexts("pt-BR").plazaAccesses(1) === "1 ponto de acesso marcado", "1 acesso");
check(getTexts("pt-BR").outlineReady(1) === "1 ponto · pronto para o planejamento", "1 ponto");
check(getTexts("pt-BR").outlineReady(4) === "4 pontos · prontos para o planejamento", "4 pontos");
check(getTexts("pt-BR").removeSelected(1) === "Remover parque (1 elemento)", "1 elemento");
check(getTexts("pt-BR").removeSelected(412) === "Remover parque (412 elementos)", "412 elementos");
check(getTexts("pt-BR").toggleCategory("Árvores", true) === "Omitir árvores", "omitir");
check(getTexts("pt-BR").toggleCategory("Árvores", false) === "Incluir árvores", "incluir");

if (failures.length > 0) {
  throw new Error(failures.join("\n"));
}
