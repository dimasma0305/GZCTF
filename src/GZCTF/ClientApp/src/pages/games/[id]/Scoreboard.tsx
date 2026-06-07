import { Alert, Center, SegmentedControl, Stack } from '@mantine/core'
import { useLocalStorage } from '@mantine/hooks'
import { mdiCrown, mdiFlagOutline, mdiSnowflake, mdiSwordCross } from '@mdi/js'
import Icon from '@mdi/react'
import dayjs from 'dayjs'
import { FC, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate, useParams } from 'react-router'
import { AdScoreboardTable } from '@Components/AdScoreboardTable'
import { KothScoreboardTable } from '@Components/KothScoreboardTable'
import { ScoreboardTable } from '@Components/ScoreboardTable'
import { TeamRank } from '@Components/TeamRank'
import { WithGameTab } from '@Components/WithGameTab'
import { WithNavBar } from '@Components/WithNavbar'
import { AdScoreTimeLine } from '@Components/charts/AdScoreTimeLine'
import { KothScoreTimeLine } from '@Components/charts/KothScoreTimeLine'
import { ScoreTimeLine } from '@Components/charts/ScoreTimeLine'
import { MobileScoreboardTable } from '@Components/mobile/ScoreboardTable'
import { useIsMobile } from '@Utils/ThemeOverride'
import { useGameScoreboard, useGameTeamInfo, useKothScoreboard } from '@Hooks/useGame'
import api from '@Api'

type ScoreboardTab = 'jeopardy' | 'ad' | 'koth'
const ALL_TABS: ScoreboardTab[] = ['jeopardy', 'ad', 'koth']
// Per-game last-tab memory key. Keyed on gameId so switching between games
// doesn't carry the wrong tab over.
const tabStorageKey = (gameId: number) => `scoreboard-tab-${gameId}`

const Scoreboard: FC = () => {
  const { id } = useParams()
  const numId = parseInt(id ?? '-1')
  const { teamInfo, error } = useGameTeamInfo(numId)
  const { scoreboard } = useGameScoreboard(numId)
  const { t } = useTranslation()
  const navigate = useNavigate()
  const location = useLocation()

  const [divisionId, setDivisionId] = useState<number | null>(null)
  const isMobile = useIsMobile(1080)
  const isVertical = useIsMobile()

  // Derive presence of each engine to pick the board(s) to show. The three boards
  // are independent: jeopardy uses ScoreboardTable, A&D uses AdScoreboardTable
  // (which includes hills as columns alongside services), KotH uses the dedicated
  // KothScoreboardTable from /Ad/Koth/Scoreboard.
  //
  // Detect from the PUBLIC scoreboard's challenge list so anonymous (logged-out)
  // visitors get the correct board — the richer teamInfo (/Details) is
  // [RequireUser]-gated and 401s for the public, which would otherwise collapse an
  // A&D/KotH game to an empty jeopardy table. Prefer teamInfo when present (logged
  // in) for parity, else fall back to the public scoreboard.
  const { hasJeopardyChallenges, hasAdChallenges, hasKothChallenges } = useMemo(() => {
    const fromTeam = Object.values(teamInfo?.challenges ?? {}).flat()
    const fromBoard = Object.values(scoreboard?.challenges ?? {}).flat()
    const all = fromTeam.length > 0 ? fromTeam : fromBoard
    return {
      hasJeopardyChallenges: all.some((c) => c.type !== 'AttackDefense' && c.type !== 'KingOfTheHill'),
      hasAdChallenges: all.some((c) => c.type === 'AttackDefense'),
      hasKothChallenges: all.some((c) => c.type === 'KingOfTheHill'),
    }
  }, [teamInfo, scoreboard])

  const presentTabs = (hasJeopardyChallenges ? 1 : 0) + (hasAdChallenges ? 1 : 0) + (hasKothChallenges ? 1 : 0)
  const showTabs = presentTabs >= 2
  // Default tab in priority: jeopardy if present, else AD, else KotH.
  const defaultTab: ScoreboardTab = hasJeopardyChallenges ? 'jeopardy' : hasAdChallenges ? 'ad' : 'koth'

  // Hash → tab parser (used on mount AND when the hash changes externally,
  // e.g. user pastes a new URL or clicks a #-link). Aliases accepted so a
  // friendly share-link form like #king-of-the-hill works too.
  const parseHash = (h: string): ScoreboardTab | null => {
    const raw = h.replace(/^#/, '').toLowerCase()
    if (raw === 'koth' || raw === 'king-of-the-hill' || raw === 'kingofthehill') return 'koth'
    if (raw === 'ad' || raw === 'attack-defense' || raw === 'attackdefense') return 'ad'
    if (raw === 'jeopardy' || raw === 'ctf') return 'jeopardy'
    return null
  }

  // Persisted active tab — per-game key so each game remembers independently.
  // Source of truth for which tab is showing. Initial hash override happens
  // in a one-shot effect below so it doesn't fight subsequent clicks.
  const [storedTab, setStoredTab] = useLocalStorage<ScoreboardTab>({
    key: tabStorageKey(numId),
    defaultValue: defaultTab,
    getInitialValueInEffect: false,
  })

  // ONE-SHOT: on mount (or on game id change), if the URL hash names a tab,
  // adopt it as the stored tab. Subsequent renders use storedTab as the
  // source of truth — so clicking a tab in the SegmentedControl actually
  // takes effect instead of being immediately overwritten by the stale hash.
  useEffect(() => {
    const fromHash = parseHash(location.hash)
    if (fromHash && fromHash !== storedTab) setStoredTab(fromHash)
    // Deliberately fires only when the game id or the *external* hash
    // changes — internal hash updates from our own setActiveTab go through
    // the navigate() below and update storedTab in the same click, so this
    // effect's storedTab dependency would just re-run a no-op.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [numId, location.hash])

  // Coerce to a tab that's actually present (e.g. localStorage said 'koth'
  // but the operator disabled all KotH challenges since last visit). Never
  // mutate storage here — read-only adjustment so a temporarily-empty kind
  // doesn't get permanently forgotten in storage.
  const effectiveTab: ScoreboardTab =
    (storedTab === 'jeopardy' && !hasJeopardyChallenges)
      || (storedTab === 'ad' && !hasAdChallenges)
      || (storedTab === 'koth' && !hasKothChallenges)
      ? defaultTab
      : storedTab

  // Single click handler: update BOTH storage and URL in one go. No useEffect
  // round-trip; clicking 'A&D' immediately renders A&D and writes #ad.
  const setActiveTab = (v: string | null) => {
    if (!v || !ALL_TABS.includes(v as ScoreboardTab)) return
    const next = v as ScoreboardTab
    setStoredTab(next)
    if (parseHash(location.hash) !== next) {
      // replace: true so the back button doesn't accumulate a step per click.
      navigate(`${location.pathname}${location.search}#${next}`, { replace: true })
    }
  }

  // Keep the URL hash aligned with what's actually showing (catches the case
  // where the resolved effectiveTab differs from storedTab — e.g. stored is
  // 'koth' but no KotH challenges exist, so we render jeopardy; the URL
  // should reflect jeopardy too). Doesn't touch storedTab on purpose; see
  // comment above.
  useEffect(() => {
    if (parseHash(location.hash) !== effectiveTab) {
      navigate(`${location.pathname}${location.search}#${effectiveTab}`, { replace: true })
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [effectiveTab])

  // Each live board freezes independently (separate endpoints) — read the
  // freeze state from whichever board we're currently showing.
  const { data: adScoreboard } = api.game.useGameAdScoreboard(numId, undefined, hasAdChallenges)
  // SWR dedupes with the fetch inside KothScoreboardTable (same key), so this is
  // free and lets the freeze banner above the board read the KotH freeze state too.
  const { kothScoreboard } = useKothScoreboard(numId, hasKothChallenges)
  const onAdTab = effectiveTab === 'ad' && hasAdChallenges
  const onKothTab = effectiveTab === 'koth' && hasKothChallenges
  const frozenView = onAdTab ? adScoreboard?.isFrozenView
    : onKothTab ? kothScoreboard?.isFrozenView
    : scoreboard?.isFrozenView
  const frozenAt = onAdTab ? adScoreboard?.freeze
    : onKothTab ? kothScoreboard?.freeze
    : scoreboard?.freeze

  const freezeBanner = frozenView ? (
    <Alert color="blue" icon={<Icon path={mdiSnowflake} size={1} />}>
      {t('game.content.frozen_banner', {
        time: frozenAt ? dayjs(frozenAt).format('LLL') : '',
      })}
    </Alert>
  ) : null

  const tabNavbar = showTabs ? (
    <Center mb="xs" mt="xs">
      <SegmentedControl
        size="sm"
        value={effectiveTab}
        onChange={(v) => v && setActiveTab(v)}
        data={[
          ...(hasJeopardyChallenges ? [{
            value: 'jeopardy',
            label: (
              <Center style={{ gap: 4 }}>
                <Icon path={mdiFlagOutline} size={0.8} color="var(--mantine-color-blue-6)" />
                <span>{t('game.content.scoreboard.tab.jeopardy', 'Jeopardy')}</span>
              </Center>
            ),
          }] : []),
          ...(hasAdChallenges ? [{
            value: 'ad',
            label: (
              <Center style={{ gap: 4 }}>
                <Icon path={mdiSwordCross} size={0.8} color="var(--mantine-color-red-6)" />
                <span>{t('game.content.scoreboard.tab.ad', 'Attack & Defense')}</span>
              </Center>
            ),
          }] : []),
          ...(hasKothChallenges ? [{
            value: 'koth',
            label: (
              <Center style={{ gap: 4 }}>
                <Icon path={mdiCrown} size={0.8} color="var(--mantine-color-violet-6)" />
                <span>{t('game.content.scoreboard.tab.koth', 'King of the Hill')}</span>
              </Center>
            ),
          }] : []),
        ]}
      />
    </Center>
  ) : null

  const showJeopardy = effectiveTab === 'jeopardy' && hasJeopardyChallenges
  const showAd = effectiveTab === 'ad' && hasAdChallenges
  const showKoth = effectiveTab === 'koth' && hasKothChallenges

  return (
    <WithNavBar width="90%" minWidth={0}>
      {isMobile ? (
        <Stack pt="md">
          {freezeBanner}
          {teamInfo && !error && <TeamRank />}
          {tabNavbar}
          {showAd ? (
            <>
              <AdScoreTimeLine divisionName={null} />
              <AdScoreboardTable numId={numId} />
            </>
          ) : showKoth ? (
            <>
              <KothScoreTimeLine divisionName={null} />
              <KothScoreboardTable numId={numId} />
            </>
          ) : isVertical ? (
            <MobileScoreboardTable divisionId={divisionId} setDivisionId={setDivisionId} />
          ) : (
            <ScoreboardTable divisionId={divisionId} setDivisionId={setDivisionId} />
          )}
        </Stack>
      ) : (
        <WithGameTab>
          <Stack pb="2rem">
            {freezeBanner}
            {tabNavbar}
            {showAd ? (
              <>
                <AdScoreTimeLine divisionName={null} />
                <AdScoreboardTable numId={numId} />
              </>
            ) : showKoth ? (
              <>
                <KothScoreTimeLine divisionName={null} />
                <KothScoreboardTable numId={numId} />
              </>
            ) : (
              <>
                {showJeopardy && <ScoreTimeLine divisionId={divisionId} />}
                <ScoreboardTable divisionId={divisionId} setDivisionId={setDivisionId} />
              </>
            )}
          </Stack>
        </WithGameTab>
      )}
    </WithNavBar>
  )
}

export default Scoreboard
