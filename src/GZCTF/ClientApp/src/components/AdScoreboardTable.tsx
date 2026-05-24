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
  useMantineTheme,
} from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { mdiAccountGroup, mdiCrosshairsGps, mdiMagnify, mdiSwordCross } from '@mdi/js'
import { Icon } from '@mdi/react'
import cx from 'clsx'
import React, { FC, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ScrollingText } from '@Components/ScrollingText'
import { useAdScoreboard, useGame } from '@Hooks/useGame'
import misc from '@Styles/Misc.module.css'
import classes from '@Styles/ScoreboardTable.module.css'

// Same Widths/Lefts cumulative-sticky math as jeopardy ScoreboardTable so the
// pinned-left columns visually line up with the jeopardy board.
// Columns: [Rank, Team(avatar+name+division), Total, Attack, Defense Loss, SLA, Captures, Times Captured]
const Widths = [60, 240, 90, 90, 110, 80, 80, 110]
const Lefts = Widths.reduce(
  (acc, cur) => {
    acc.push(acc[acc.length - 1] + cur)
    return acc
  },
  [0]
)

const ITEM_COUNT_PER_PAGE = 30

interface AdScoreboardTableProps {
  numId: number
}

export const AdScoreboardTable: FC<AdScoreboardTableProps> = ({ numId }) => {
  const { t } = useTranslation()
  const theme = useMantineTheme()
  const { adScoreboard } = useAdScoreboard(numId)
  const { game } = useGame(numId)
  const myTeamName = game?.teamName ?? null

  const [activePage, setPage] = useState(1)
  const [divisionName, setDivisionName] = useState<string | null>(null)
  const [keyword, setKeyword] = useState('')
  const [debouncedKeyword] = useDebouncedValue(keyword, 400)
  const [highlightedTeam, setHighlightedTeam] = useState<string | null>(null)

  // Divisions appear on rows; the player's A&D scoreboard endpoint doesn't
  // expose a divisions array (unlike the jeopardy scoreboard), so derive from rows.
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

  const headers = [
    t('game.content.scoreboard.ad.column.rank', '#'),
    t('game.content.scoreboard.ad.column.team', 'Team'),
    t('game.content.scoreboard.ad.column.total', 'Total'),
    t('game.content.scoreboard.ad.column.attack', 'Attack'),
    t('game.content.scoreboard.ad.column.defense_loss', 'Defense loss'),
    t('game.content.scoreboard.ad.column.sla', 'SLA'),
    t('game.content.scoreboard.ad.column.captures', 'Captures'),
    t('game.content.scoreboard.ad.column.times_captured', 'Times captured'),
  ]

  return (
    <Paper shadow="md" p="md">
      <Stack gap="xs">
        {/* Toolbar — mirrors jeopardy ScoreboardTable.tsx:343-398 */}
        <Grid>
          <Grid.Col span={3}>
            <Select
              defaultValue="all"
              data={[{ value: 'all', label: t('game.label.score_table.all_teams', 'All teams') }, ...divisionOptions]}
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
            <Table className={classes.table}>
              <Table.Thead className={classes.thead}>
                <Table.Tr>
                  {headers.map((header, idx) => (
                    <Table.Th
                      key={idx}
                      className={cx(classes.left, classes.header)}
                      style={{
                        left: Lefts[idx],
                        width: Widths[idx],
                        minWidth: Widths[idx],
                        maxWidth: Widths[idx],
                      }}
                    >
                      {header}
                    </Table.Th>
                  ))}
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {currentItems.map((row) => {
                  const isHighlighted = highlightedTeam === row.teamName
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
                      {/* Rank — top 3 get colored badge, rest mono */}
                      <Table.Td
                        className={cx(classes.mono, classes.left)}
                        style={{ left: Lefts[0] }}
                      >
                        {row.rank <= 3 ? (
                          <Badge
                            color={['yellow', 'gray', 'orange'][row.rank - 1]}
                            variant="filled"
                          >
                            {row.rank}
                          </Badge>
                        ) : (
                          row.rank
                        )}
                      </Table.Td>

                      {/* Team — avatar + scrolling name + division, identical layout to jeopardy */}
                      <Table.Td className={classes.left} style={{ left: Lefts[1] }}>
                        <Group justify="left" gap={5} wrap="nowrap" maw={Widths[1] - 10}>
                          <Avatar
                            imageProps={{ loading: 'lazy' }}
                            alt="avatar"
                            radius="xl"
                            size={30}
                            color={theme.primaryColor}
                          >
                            {row.teamName?.slice(0, 1) ?? 'T'}
                          </Avatar>
                          <Stack gap={0} h="2.5rem" justify="center" w={Widths[1] - 45}>
                            <ScrollingText size="sm" text={row.teamName || ''} />
                            {!!row.division && (
                              <Text size="xs" c="dimmed" ta="start" truncate className={classes.text}>
                                {row.division}
                              </Text>
                            )}
                          </Stack>
                        </Group>
                      </Table.Td>

                      {/* Total — bold mono (the headline number) */}
                      <Table.Td
                        className={cx(classes.mono, classes.left)}
                        style={{ left: Lefts[2] }}
                      >
                        {row.total.toFixed(1)}
                      </Table.Td>

                      {/* Attack points — teal */}
                      <Table.Td className={classes.mono}>
                        <Text size="sm" c="teal" className={misc.ffmono} fw={700}>
                          +{row.attackPoints.toFixed(1)}
                        </Text>
                      </Table.Td>

                      {/* Defense loss — red if non-zero, dimmed otherwise */}
                      <Table.Td className={classes.mono}>
                        <Text
                          size="sm"
                          c={row.defenseLoss > 0 ? 'red' : 'dimmed'}
                          className={misc.ffmono}
                          fw={700}
                        >
                          {row.defenseLoss > 0 ? `−${row.defenseLoss.toFixed(1)}` : '0.0'}
                        </Text>
                      </Table.Td>

                      {/* SLA — blue */}
                      <Table.Td className={classes.mono}>
                        <Text size="sm" c="blue" className={misc.ffmono} fw={700}>
                          {row.slaPoints.toFixed(1)}
                        </Text>
                      </Table.Td>

                      {/* Flags captured — plain mono */}
                      <Table.Td className={classes.mono}>{row.flagsCaptured}</Table.Td>

                      {/* Times captured — red when >0 */}
                      <Table.Td className={classes.mono}>
                        <Text
                          size="sm"
                          c={row.timesCaptured > 0 ? 'red' : 'dimmed'}
                          className={misc.ffmono}
                          fw={700}
                        >
                          {row.timesCaptured}
                        </Text>
                      </Table.Td>
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

        {/* Footer — pagination + tip, mirrors jeopardy ScoreboardTable.tsx:449-459 */}
        <Group justify="space-between">
          <Text size="sm" c="dimmed">
            {t(
              'game.content.scoreboard.ad.tip',
              'A&D ranking updates after every check + every accepted attack.'
            )}
          </Text>
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
