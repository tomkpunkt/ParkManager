import { de } from "./locales/de";
import { en } from "./locales/en";
import type { Texts } from "./locales/en";
import { ptBR } from "./locales/pt-BR";

export type { Texts };

const translations: { [locale: string]: Texts } = {
  en,
  de,
  "pt-BR": ptBR,
};

const languagePrefix = (locale: string): string => {
  const hyphen = locale.indexOf("-");
  const underscore = locale.indexOf("_");
  const cut = hyphen === -1 ? underscore
    : underscore === -1 ? hyphen
      : Math.min(hyphen, underscore);
  return cut > 0 ? locale.slice(0, cut) : "";
};

const translationFor = (locale: string): Texts | undefined =>
  Object.prototype.hasOwnProperty.call(translations, locale)
    ? translations[locale] : undefined;

export const getTexts = (locale: string): Texts => {
  const id = typeof locale === "string" ? locale.trim() : "";
  const exact = id ? translationFor(id) : undefined;
  if (exact) return exact;
  const byLanguage = translationFor(languagePrefix(id));
  return byLanguage ?? en;
};
