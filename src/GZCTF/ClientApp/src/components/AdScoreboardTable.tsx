import {
  alpha,
  Avatar,
  Badge,
  Box,
  Button,
  Center,
  Grid,
  Group,
  Pagination,
  Paper,
  Select,
  Stack,
  Table,
  Text,
  TextInput,
  Tooltip,
  useMantineColorScheme,
  useMantineTheme,
} from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { mdiAccountGroup, mdiCrosshairsGps, mdiHeartPulse, mdiMagnify, mdiShieldHalfFull, mdiSwordCross, mdiTimerSandComplete } from '@mdi/js'
import { Icon } from '@mdi/react'
import cx from 'clsx'
import React, { FC, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ScrollingText } from '@Components/ScrollingText'
import { useAdScoreboard, useGame } from '@Hooks/useGame'
import { useChallengeCategoryLabelMap } from '@Utils/Shared'
import { AdScoreboardChallenge, ChallengeCategory } from '@Api'
import misc from '@Styles/Misc.module.css'
import classes from '@Styles/ScoreboardTable.module.css'

// Same Widths/Lefts cumulative-sticky math as jeopardy ScoreboardTable so the
// pinned-left columns visually line up with the jeopardy board.
// Pinned columns: [Rank overall, Rank division, Team, Captures, Total].
// Rank/division kept tight so the left block doesn't crowd out the services.
const Widths = [44, 44, 150, 56, 64]
const Lefts = Widths.reduce(
  (acc, cur) => {
    acc.push(acc[acc.length - 1] + cur)
    return acc
  },
  [0]
)

const ITEM_COUNT_PER_PAGE = 30

// Fixed sub-column widths (px) for each service group. With table-layout:fixed
// the tier-2 icon header + the body numbers share these widths, so a tight
// icon sits directly over its number — the icons cluster instead of spreading
// across one wide cell.
const SUBCOL = { atk: 40, sla: 46, def: 42, status: 56 }

// A challenge group spans its 4 metric sub-columns. Bound the tier-2 name to
// this width so a long challenge title truncates within its own group instead
// of overflowing into the neighbouring challenge's header (visible overlap once
// there are several challenges).
const GROUP_W = SUBCOL.atk + SUBCOL.sla + SUBCOL.def + SUBCOL.status

// Compact point format: integers for large values (SLA), one decimal for the
// small ones (attack / defense) — keeps the sub-columns narrow.
const fmtPts = (n: number) => (Math.abs(n) >= 100 ? Math.round(n).toString() : n.toFixed(1))

// Per-service status dot color, matching the challenge-panel badge palette.
const statusColor = (s?: string | null) => {
  switch (s) {
    case 'Ok':
      return 'teal'
    case 'Mumble':
      return 'yellow'
    case 'Offline':
      return 'red'
    default:
      return 'gray' // InternalError / never-checked
  }
}

interface AdScoreboardTableProps {
  numId: number
}

export const AdScoreboardTable: FC<AdScoreboardTableProps> = ({ numId }) => {
  const { t } = useTranslation()
  const theme = useMantineTheme()
  const { colorScheme } = useMantineColorScheme()
  const categoryLabelMap = useChallengeCategoryLabelMap()
  const { adScoreboard } = useAdScoreboard(numId)
  const { game } = useGame(numId)

  // Group the service columns by category (the backend already orders them
  // contiguously) so the header can render a colored category tier like the
  // jeopardy board.
  const challengeGroups = useMemo(() => {
    const out: { category: string; items: AdScoreboardChallenge[] }[] = []
    for (const ch of adScoreboard?.challenges ?? []) {
      const last = out[out.length - 1]
      if (last && last.category === ch.category) last.items.push(ch)
      else out.push({ category: ch.category, items: [ch] })
    }
    return out
  }, [adScoreboard])
  const myTeamName = game?.teamName ?? null

  const [activePage, setPage] = useState(1)
  const [divisionName, setDivisionName] = useState<string | null>(null)
  const [keyword, setKeyword] = useState('')
  const [debouncedKeyword] = useDebouncedValue(keyword, 400)
  const [highlightedTeam, setHighlightedTeam] = useState<string | null>(null)

  // Divisions appear on rows; the A&D scoreboard endpoint doesn't expose a
  // divisions array (unlike the jeopardy scoreboard), so derive from rows.
  const divisionOptions = useMemo(() => {
    if (!adScoreboard) return []
    const seen = new Set<string>()
    const out: { value: string; label: string }[] = []
    for (const row of adScoreboard.teams) {
      if (row.division && !seen.has(row.division)) {
        seen.add(row.division)
        out.push({ value: row.division, label: row.division })
      }
    }
    return out
  }, [adScoreboard])

  const selectValue = divisionName ?? 'all'

  const filteredList = useMemo(() => {
    if (!adScoreboard?.teams) return []
    const kw = debouncedKeyword.trim().toLowerCase()
    let list = adScoreboard.teams
    if (kw.length > 0) {
      list = list.filter((s) => s.teamName?.toLowerCase().includes(kw))
    } else if (divisionName !== null) {
      list = list.filter((s) => s.division === divisionName)
    }
    return list
  }, [adScoreboard, debouncedKeyword, divisionName])

  useEffect(() => {
    setPage(1)
  }, [debouncedKeyword, divisionName])

  const base = (activePage - 1) * ITEM_COUNT_PER_PAGE
  const currentItems = filteredList.slice(base, base + ITEM_COUNT_PER_PAGE)

  const hasDivisionFilter = divisionOptions.length > 0
  const allRank = divisionName === null

  // Empty sticky placeholders for the pinned columns in the category + name
  // header tiers (same trick as jeopardy ScoreboardTable) so the pinned-left
  // cells stay aligned across all three header rows.
  const hiddenCol = [...Array(5).keys()].map((i) => (
    <Table.Th
      key={`hidden-${i}`}
      className={classes.left}
      style={{ left: Lefts[i], width: Widths[i], minWidth: Widths[i], maxWidth: Widths[i] }}
    >
      &nbsp;
    </Table.Th>
  ))

  if (!adScoreboard || adScoreboard.teams.length === 0 || adScoreboard.latestRound === 0) {
    return (
      <Paper shadow="md" p="xl">
        <Stack align="center" gap="xs">
          <Icon path={mdiSwordCross} size={2.5} color="var(--mantine-color-dimmed)" />
          <Text fw="bold" c="dimmed">
            {t('game.content.scoreboard.ad.empty.title', 'No A&D rounds yet')}
          </Text>
          <Text size="sm" c="dimmed">
            {t(
              'game.content.scoreboard.ad.empty.description',
              'The board will fill in once the first round completes.'
            )}
          </Text>
        </Stack>
      </Paper>
    )
  }

  return (
    <Paper shadow="md" p="md">
      <Stack gap="xs">
        {/* Toolbar — mirrors jeopardy ScoreboardTable.tsx:343-398 */}
        <Grid>
          <Grid.Col span={3}>
            <Select
              defaultValue="all"
              data={[
                { value: 'all', label: t('game.label.score_table.all_teams', 'All teams') },
                ...divisionOptions,
              ]}
              value={selectValue}
              readOnly={!hasDivisionFilter}
              onChange={(div) => setDivisionName(!div || div === 'all' ? null : div)}
              leftSection={<Icon path={mdiAccountGroup} size={1} />}
            />
          </Grid.Col>
          <Grid.Col span={4}>
            {myTeamName && (
              <Button
                variant="light"
                leftSection={<Icon path={mdiCrosshairsGps} size={0.9} />}
                onClick={() => {
                  const idx = filteredList.findIndex((it) => it.teamName === myTeamName)
                  if (idx < 0) return
                  const page = Math.floor(idx / ITEM_COUNT_PER_PAGE) + 1
                  setPage(page)
                  setHighlightedTeam(myTeamName)
                  requestAnimationFrame(() => {
                    const el = document.querySelector(
                      `[data-team-name="${CSS.escape(myTeamName)}"]`
                    )
                    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'center' })
                  })
                  setTimeout(() => setHighlightedTeam(null), 2500)
                }}
              >
                {t('game.button.find_my_team', 'Find My Team')}
              </Button>
            )}
          </Grid.Col>
          <Grid.Col span={2}>
            <Group justify="flex-end" gap="xs" h="100%">
              <Text size="xs" c="dimmed">
                {t('game.content.scoreboard.ad.latest_round', {
                  round: adScoreboard.latestRound,
                  defaultValue: 'through round {{round}}',
                })}
              </Text>
            </Group>
          </Grid.Col>
          <Grid.Col span={3}>
            <TextInput
              placeholder={t('game.placeholder.search_team', 'Search Team')}
              value={keyword}
              onChange={(e) => setKeyword(e.currentTarget.value)}
              leftSection={<Icon path={mdiMagnify} size={1} />}
            />
          </Grid.Col>
        </Grid>

        <Box pos="relative" mih="calc(100vh - 14rem)">
          <Table.ScrollContainer
            minWidth="100%"
            classNames={{ scrollContainer: misc.noScrollBars }}
          >
            <Table className={classes.table} verticalSpacing={4} horizontalSpacing={8}>
              <Table.Thead className={classes.thead}>
                {/* Tier 1 — colored category groups (icon + name), mirroring the
                    jeopardy board. Pinned columns are empty placeholders here. */}
                <Table.Tr className={misc.noBorder}>
                  {hiddenCol}
                  {challengeGroups.map((grp) => {
                    const cate = categoryLabelMap.get(grp.category as ChallengeCategory)
                    return (
                      <Table.Th
                        key={grp.category}
                        colSpan={grp.items.length * 4}
                        className={classes.groupStart}
                        h="2.4rem"
                        style={
                          cate
                            ? {
                                backgroundColor: alpha(
                                  theme.colors[cate.color][colorScheme === 'dark' ? 8 : 6],
                                  colorScheme === 'dark' ? 0.15 : 0.2
                                ),
                              }
                            : undefined
                        }
                      >
                        <Group gap={4} wrap="nowrap" justify="center" w="100%">
                          {cate && (
                            <Icon
                              path={cate.icon}
                              size={0.8}
                              color={theme.colors[cate.color][colorScheme === 'dark' ? 8 : 6]}
                            />
                          )}
                          <Text c={cate?.color} className={classes.text} ff="text" fz="xs">
                            {grp.category}
                          </Text>
                        </Group>
                      </Table.Th>
                    )
                  })}
                  {/* Flexible spacer — soaks up the surplus when the table is
                      forced to min-width:100% with few challenges, so the
                      fixed-width pinned + metric columns don't stretch. Spans
                      all three header rows. */}
                  <Table.Th rowSpan={3} aria-hidden />
                </Table.Tr>
                {/* Tier 2 — challenge name, spanning its 4 metric sub-columns. */}
                <Table.Tr>
                  {hiddenCol}
                  {(adScoreboard.challenges ?? []).map((ch) => (
                    <Table.Th key={ch.challengeId} colSpan={4} className={cx(classes.mono, classes.groupStart)}>
                      <Tooltip label={ch.title} withinPortal>
                        <Text size="xs" fw={700} truncate maw={GROUP_W} mx="auto">
                          {ch.title}
                        </Text>
                      </Tooltip>
                    </Table.Th>
                  ))}
                </Table.Tr>
                {/* Tier 3 — pinned column labels + metric icons (attack / SLA /
                    defense / status) as narrow sub-columns. */}
                <Table.Tr>
                  {[
                    t('game.label.score_table.rank_total', 'Rank'),
                    t('game.label.score_table.rank_division', 'Division'),
                    t('common.label.team', 'Team'),
                    t('game.content.scoreboard.ad.column.captures', 'Captures'),
                    t('game.content.scoreboard.ad.column.total', 'Total'),
                  ].map((header, idx) => (
                    <Table.Th
                      key={idx}
                      className={cx(classes.left, classes.header)}
                      style={{ left: Lefts[idx] }}
                    >
                      {header}
                    </Table.Th>
                  ))}
                  {(adScoreboard.challenges ?? []).flatMap((ch) => [
                    <Table.Th key={`${ch.challengeId}-a`} className={cx(classes.mono, classes.groupStart)} style={{ width: SUBCOL.atk }}>
                      <Tooltip label={t('game.content.scoreboard.ad.legend.attack', 'Attack')} withinPortal>
                        <Center><Icon path={mdiSwordCross} size={0.6} color={theme.colors.teal[6]} /></Center>
                      </Tooltip>
                    </Table.Th>,
                    <Table.Th key={`${ch.challengeId}-s`} className={classes.mono} style={{ width: SUBCOL.sla }}>
                      <Tooltip label={t('game.content.scoreboard.ad.legend.sla', 'SLA')} withinPortal>
                        <Center><Icon path={mdiTimerSandComplete} size={0.6} color={theme.colors.blue[6]} /></Center>
                      </Tooltip>
                    </Table.Th>,
                    <Table.Th key={`${ch.challengeId}-d`} className={classes.mono} style={{ width: SUBCOL.def }}>
                      <Tooltip label={t('game.content.scoreboard.ad.legend.defense', 'Defense loss')} withinPortal>
                        <Center><Icon path={mdiShieldHalfFull} size={0.6} color={theme.colors.red[6]} /></Center>
                      </Tooltip>
                    </Table.Th>,
                    <Table.Th key={`${ch.challengeId}-st`} className={classes.mono} style={{ width: SUBCOL.status }}>
                      <Tooltip label={t('game.content.scoreboard.ad.column.status', 'Status')} withinPortal>
                        <Center><Icon path={mdiHeartPulse} size={0.6} color="var(--mantine-color-dimmed)" /></Center>
                      </Tooltip>
                    </Table.Th>,
                  ])}
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {currentItems.map((row, idx) => {
                  const isHighlighted = highlightedTeam === row.teamName
                  const tableRank = base + idx + 1
                  return (
                    <Table.Tr
                      key={row.participationId}
                      data-team-name={row.teamName}
                      style={
                        isHighlighted
                          ? {
                              outline: `2px solid ${theme.colors[theme.primaryColor][4]}`,
                              outlineOffset: -2,
                              background: alpha(theme.colors[theme.primaryColor][4], 0.12),
                              transition: 'background .3s ease, outline .3s ease',
                            }
                          : undefined
                      }
                    >
                      {/* Overall rank — plain mono (same as jeopardy, no colored badge) */}
                      <Table.Td
                        className={cx(classes.mono, classes.left)}
                        style={{ left: Lefts[0] }}
                      >
                        {row.rank || '-'}
                      </Table.Td>

                      {/* Division / table rank — when filtered by division, sequence within the page;
                          otherwise mirror the overall rank, matching jeopardy's behavior. */}
                      <Table.Td
                        className={cx(classes.mono, classes.left)}
                        style={{ left: Lefts[1] }}
                      >
                        {allRank ? row.rank : tableRank}
                      </Table.Td>

                      {/* Team — avatar + scrolling name + division line */}
                      <Table.Td className={classes.left} style={{ left: Lefts[2] }}>
                        <Group justify="left" gap={5} wrap="nowrap" maw={Widths[2] - 10}>
                          <Avatar
                            imageProps={{ loading: 'lazy' }}
                            alt="avatar"
                            radius="xl"
                            size={30}
                            color={theme.primaryColor}
                          >
                            {row.teamName?.slice(0, 1) ?? 'T'}
                          </Avatar>
                          <Stack gap={0} h="2.5rem" justify="center" w={Widths[2] - 45}>
                            <ScrollingText size="sm" text={row.teamName || ''} />
                            {!!row.division && (
                              <Text
                                size="xs"
                                c="dimmed"
                                ta="start"
                                truncate
                                className={classes.text}
                              >
                                {row.division}
                              </Text>
                            )}
                          </Stack>
                        </Group>
                      </Table.Td>

                      {/* Captures — plain mono (the "solved count" equivalent) */}
                      <Table.Td
                        className={cx(classes.mono, classes.left)}
                        style={{ left: Lefts[3] }}
                      >
                        {row.flagsCaptured}
                      </Table.Td>

                      {/* Total — bold mono (the headline score, mirrors jeopardy score_total) */}
                      <Table.Td
                        className={cx(classes.mono, classes.left)}
                        style={{ left: Lefts[4] }}
                      >
                        {row.total.toFixed(1)}
                      </Table.Td>

                      {/* Per-service cells — four real sub-columns (attack / SLA /
                          defense / status) per challenge, aligned under the icon
                          headers. The numbers replace the redundant team-level
                          aggregate columns; Total (pinned) is the rollup. */}
                      {(adScoreboard.challenges ?? []).flatMap((ch) => {
                        const svc = row.services?.find((s) => s.challengeId === ch.challengeId)
                        if (!svc) {
                          return [
                            <Table.Td key={ch.challengeId} colSpan={4} className={cx(classes.mono, classes.groupStart)}>
                              <Text size="xs" c="dimmed">
                                {t('game.content.scoreboard.ad.no_service_cell', 'no service')}
                              </Text>
                            </Table.Td>,
                          ]
                        }
                        return [
                          <Table.Td key={`${ch.challengeId}-a`} className={cx(classes.mono, classes.groupStart)}>
                            <Text size="xs" c="teal" className={misc.ffmono} fw={700}>
                              {fmtPts(svc.attackPoints)}
                            </Text>
                          </Table.Td>,
                          <Table.Td key={`${ch.challengeId}-s`} className={classes.mono}>
                            <Text size="xs" c="blue" className={misc.ffmono} fw={700}>
                              {fmtPts(svc.slaPoints)}
                            </Text>
                          </Table.Td>,
                          <Table.Td key={`${ch.challengeId}-d`} className={classes.mono}>
                            <Text
                              size="xs"
                              c={svc.defenseLoss > 0 ? 'red' : 'dimmed'}
                              className={misc.ffmono}
                              fw={700}
                            >
                              {svc.defenseLoss > 0 ? `−${fmtPts(svc.defenseLoss)}` : '0'}
                            </Text>
                          </Table.Td>,
                          <Table.Td key={`${ch.challengeId}-st`} className={classes.mono}>
                            <Badge
                              size="xs"
                              variant="light"
                              color={statusColor(svc.lastCheckStatus)}
                              px={5}
                              styles={{ root: { textTransform: 'none' }, label: { fontSize: 9 } }}
                            >
                              {svc.lastCheckStatus === 'InternalError'
                                ? 'Error'
                                : (svc.lastCheckStatus ??
                                  t('game.content.scoreboard.ad.cell.no_check', 'n/a'))}
                            </Badge>
                          </Table.Td>,
                        ]
                      })}
                      {/* matches the flexible spacer column in the header */}
                      <Table.Td aria-hidden />
                    </Table.Tr>
                  )
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>

          {filteredList.length === 0 && (
            <Center mih="6rem">
              <Text size="sm" c="dimmed">
                {t('game.content.scoreboard.ad.no_teams', 'No teams ranked yet.')}
              </Text>
            </Center>
          )}
        </Box>

        {/* Footer — stats legend + tip on the left, pagination on the right.
            Jeopardy floats its bloods legend absolutely over the top-left empty
            header cells, but A&D has only one header row so an inline footer
            keeps the legend visible without obscuring the table. */}
        <Group justify="space-between" align="flex-start" wrap="wrap" gap="md">
          <Stack gap={4}>
            <Tooltip.Group>
              <Group gap="lg">
                <Tooltip
                  label={t(
                    'game.content.scoreboard.ad.legend.attack_tip',
                    'Points gained from flags captured from other teams.'
                  )}
                  transitionProps={{ transition: 'pop' }}
                >
                  <Group justify="left" gap={4}>
                    <Icon path={mdiSwordCross} size={0.8} color={theme.colors.teal[6]} />
                    <Text size="sm" c="teal">
                      {t('game.content.scoreboard.ad.legend.attack', 'Attack')}
                    </Text>
                  </Group>
                </Tooltip>
                <Tooltip
                  label={t(
                    'game.content.scoreboard.ad.legend.defense_tip',
                    'Points lost from your services being captured by others.'
                  )}
                  transitionProps={{ transition: 'pop' }}
                >
                  <Group justify="left" gap={4}>
                    <Icon path={mdiShieldHalfFull} size={0.8} color={theme.colors.red[6]} />
                    <Text size="sm" c="red">
                      {t('game.content.scoreboard.ad.legend.defense', 'Defense loss')}
                    </Text>
                  </Group>
                </Tooltip>
                <Tooltip
                  label={t(
                    'game.content.scoreboard.ad.legend.sla_tip',
                    'Service-level availability points from passing checks.'
                  )}
                  transitionProps={{ transition: 'pop' }}
                >
                  <Group justify="left" gap={4}>
                    <Icon path={mdiTimerSandComplete} size={0.8} color={theme.colors.blue[6]} />
                    <Text size="sm" c="blue">
                      {t('game.content.scoreboard.ad.legend.sla', 'SLA')}
                    </Text>
                  </Group>
                </Tooltip>
              </Group>
            </Tooltip.Group>
            <Text size="xs" c="dimmed">
              {t(
                'game.content.scoreboard.ad.tip',
                'Total = Attack + SLA − Defense loss. Updated after every check + every accepted attack.'
              )}
            </Text>
          </Stack>
          <Pagination
            value={activePage}
            onChange={setPage}
            total={Math.max(1, Math.ceil(filteredList.length / ITEM_COUNT_PER_PAGE))}
            boundaries={2}
          />
        </Group>
      </Stack>
    </Paper>
  )
}
